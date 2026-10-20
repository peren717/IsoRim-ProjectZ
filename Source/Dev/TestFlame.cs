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
    // Mode aimfloor (alias flamediag; run with -projectzfloors=3 -projectzseed=7): directional effects between floors
    // (AimFloor). A 7x7 tower with a slab on floor 2 at the south of a cleared field. Colonist A on floor 2 at the
    // tower's north edge, B on the ground north of it. Every case is a real order (ability cast job, AttackStatic, a
    // turret's verb) run on real ticks against a stunned raider, each case with its own random seed (seed ^ name) and a
    // cleaned field. Recorded: fires started, flame/acid/beam damage, beam hit cells, projectile launches.
    // Between floors (checks per case):
    //   lands      something burns/hits (only when the target is seen from the shooter: Los3D);
    //   floor      everything lands on the target's floor (no other floor, no void row);
    //   toward     the centroid of what lands lies toward the target over the ground (within 35 degrees);
    //   hidden     nothing lands on a cell the shooter can't see (under the slab);
    //   target     the raider is hit exactly when the shooter sees its cell;
    //   source     projectiles (sprays) leave from the shooter's own floor;
    //   cone       (abilities) the previewed cells: target's floor, toward the target, all seen.
    // Same floor (controls): no AimFloor scope opened (the game's own code ran), the raider hit, all on its floor.
    // Screenshots in the 3D view at the cast (a close and a wide one).
    public static partial class TestRunner
    {
        private sealed class FlameCase
        {
            public string name;
            public string ability;   // AbilityDef (cast), or null
            public string gun;       // weapon (AttackStatic), or null
            public string turret;    // turret building (its verb started on the raider), or null
            public char shooter;     // 'A' floor 2 (tower edge), 'B' ground, 'T' a turret
            public int tdx, tdz;     // target ground offset from the tower's north edge centre
            public int tk;           // target floor (0 ground, 1 floor 2)
            public int bdx, bdz;     // B's (or the turret's) ground offset
            public int sk;           // turret floor
            public bool thrown;      // a thrown explosive (no AimFloor scope: its own flight, Flights)
            public string twin;      // with a canopy (FzCanopy) hiding cells this earlier case (no canopy) burned
            public bool Canopy => twin != null;
            public bool Cross => tk != (shooter == 'A' ? 1 : shooter == 'T' ? sk : 0);
        }

        private static readonly FlameCase[] fzCases =
        {
            new FlameCase { name = "firespew_down", ability = "FireSpew", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "firespew_down_east", ability = "FireSpew", shooter = 'A', tdx = 5, tdz = 1, tk = 0 },
            new FlameCase { name = "firespew_up", ability = "FireSpew", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 4 },
            new FlameCase { name = "firespew_flat", ability = "FireSpew", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "acidspray_down", ability = "AcidSpray", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "acidspray_up", ability = "AcidSpray", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 4 },
            new FlameCase { name = "acidspray_flat", ability = "AcidSpray", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "hellcatburner_down", ability = "HellcatBurner", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "hellcatburner_up", ability = "HellcatBurner", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 4 },
            new FlameCase { name = "hellcatburner_flat", ability = "HellcatBurner", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "incineratorburner_down", ability = "IncineratorBurner", shooter = 'A', tdx = 3, tdz = 6, tk = 0 },
            new FlameCase { name = "incinerator_down", gun = "Gun_Incinerator", shooter = 'A', tdx = 4, tdz = 8, tk = 0 },
            new FlameCase { name = "incinerator_up", gun = "Gun_Incinerator", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 7 },
            new FlameCase { name = "incinerator_flat", gun = "Gun_Incinerator", shooter = 'B', tdx = 4, tdz = 12, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "graser_down", gun = "Gun_BeamGraser", shooter = 'A', tdx = 4, tdz = 8, tk = 0 },
            new FlameCase { name = "graser_up", gun = "Gun_BeamGraser", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 7 },
            new FlameCase { name = "graser_flat", gun = "Gun_BeamGraser", shooter = 'B', tdx = 4, tdz = 12, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "miniflame_down", gun = "Gun_MiniFlameblaster", shooter = 'A', tdx = 2, tdz = 3, tk = 0 },
            new FlameCase { name = "miniflame_up", gun = "Gun_MiniFlameblaster", shooter = 'B', tdx = 1, tdz = -1, tk = 1, bdx = 0, bdz = 3 },
            new FlameCase { name = "miniflame_flat", gun = "Gun_MiniFlameblaster", shooter = 'B', tdx = 2, tdz = 6, tk = 0, bdx = 0, bdz = 3 },
            new FlameCase { name = "foamturret_down", turret = "Turret_FoamTurret", shooter = 'T', sk = 1, bdx = -2, bdz = 0, tdx = 3, tdz = 7, tk = 0 },
            new FlameCase { name = "foamturret_up", turret = "Turret_FoamTurret", shooter = 'T', sk = 0, bdx = -2, bdz = 6, tdx = 2, tdz = -1, tk = 1 },
            new FlameCase { name = "foamturret_flat", turret = "Turret_FoamTurret", shooter = 'T', sk = 0, bdx = -2, bdz = 4, tdx = 3, tdz = 10, tk = 0 },
            // a canopy (a floor-2 slab) over part of the twin case's burned cells: those are hidden from A, left alone
            new FlameCase { name = "firespew_down_canopy", ability = "FireSpew", shooter = 'A', tdx = 3, tdz = 4, tk = 0, twin = "firespew_down" },
            new FlameCase { name = "incinerator_down_canopy", gun = "Gun_Incinerator", shooter = 'A', tdx = 4, tdz = 8, tk = 0, twin = "incinerator_down" },
            // thrown explosives (user request): explode on the target's floor at the target, effects only there, a sane arc
            new FlameCase { name = "frag_down", gun = "Weapon_GrenadeFrag", thrown = true, shooter = 'A', tdx = 3, tdz = 7, tk = 0 },
            new FlameCase { name = "frag_up", gun = "Weapon_GrenadeFrag", thrown = true, shooter = 'B', tdx = 0, tdz = -3, tk = 1, bdx = -2, bdz = 7 },
            new FlameCase { name = "frag_flat", gun = "Weapon_GrenadeFrag", thrown = true, shooter = 'B', tdx = 3, tdz = 10, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "molotov_down", gun = "Weapon_GrenadeMolotov", thrown = true, shooter = 'A', tdx = 3, tdz = 7, tk = 0 },
            new FlameCase { name = "molotov_up", gun = "Weapon_GrenadeMolotov", thrown = true, shooter = 'B', tdx = 0, tdz = -3, tk = 1, bdx = -2, bdz = 7 },
            new FlameCase { name = "molotov_flat", gun = "Weapon_GrenadeMolotov", thrown = true, shooter = 'B', tdx = 3, tdz = 10, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "emp_down", gun = "Weapon_GrenadeEMP", thrown = true, shooter = 'A', tdx = 3, tdz = 7, tk = 0 },
            new FlameCase { name = "emp_up", gun = "Weapon_GrenadeEMP", thrown = true, shooter = 'B', tdx = 0, tdz = -3, tk = 1, bdx = -2, bdz = 7 },
            new FlameCase { name = "emp_flat", gun = "Weapon_GrenadeEMP", thrown = true, shooter = 'B', tdx = 3, tdz = 10, tk = 0, bdx = -2, bdz = 4 },
        };

        private static readonly Dictionary<string, List<IntVec3>> fzLandedBy = new Dictionary<string, List<IntVec3>>();
        private static readonly List<IntVec3> fzCanopy = new List<IntVec3>();
        private static readonly List<IntVec3> fzBlasts = new List<IntVec3>();
        private static readonly List<Vector3> fzArc = new List<Vector3>();   // thrown: ground x, true height, ground z per tick
        private static Projectile fzProj;
        private static bool fzThrownNow;   // thrown: only the thrower's damage counts (not a fall through a broken slab)

        public static void FzBlast_Prefix(IntVec3 center, Thing instigator)
        {
            if (fzRec && instigator != null && instigator == fzShooter)
            {
                fzBlasts.Add(center);
            }
        }

        public static void FzProjTick_Postfix(Projectile __instance)
        {
            if (!fzRec || fzShooter == null || __instance.Launcher != fzShooter || !__instance.Spawned)
            {
                return;
            }
            if (fzProj != null && fzProj != __instance)
            {
                return;   // the first throw only
            }
            fzProj = __instance;
            Vector3 at = __instance.ExactPosition;
            IntVec3 g = ProjectZ.GroundCellOf(at.ToIntVec3(), __instance.Map);
            fzArc.Add(new Vector3(g.x + (at.x - Mathf.Floor(at.x)), IsoRimBridge.ProjectileHeight(__instance, at), g.z + (at.z - Mathf.Floor(at.z))));
        }

        // a thrown projectile is half way (for the screenshot of its arc)
        private static bool FzMidFlight()
        {
            if (fzProj == null || !fzProj.Spawned)
            {
                return fzAppliedAt >= 0 && Find.TickManager.TicksGame - fzAppliedAt >= 40;
            }
            Traverse t = Traverse.Create(fzProj);
            return t.Field("ticksToImpact").GetValue<int>() <= t.Property("StartingTicksToImpact").GetValue<float>() * 0.5f;
        }

        private static Pawn fzA, fzB, fzH;
        private static Building fzTurret;
        private static Thing fzShooter;
        private static int fzCx, fzEz, fzCase, fzAppliedAt, fzBeamShots, fzFrame, fzScopes0;
        private static bool fzRec;
        private static CellRect fzTower;
        private static readonly List<IntVec3> fzFires = new List<IntVec3>();
        private static readonly List<string> fzDamage = new List<string>();
        private static readonly List<IntVec3> fzHitCells = new List<IntVec3>();
        private static readonly List<IntVec3> fzBeamHits = new List<IntVec3>();
        private static readonly List<IntVec3> fzLaunchDest = new List<IntVec3>();
        private static readonly List<IntVec3> fzLaunchFrom = new List<IntVec3>();
        private static List<IntVec3> fzCone;
        private static Map fzMap;
        private static bool fzPatched;

        private static IntVec3 FzEdge(Map map) => ProjectZ.CellOnFloor(new IntVec3(fzCx, 0, fzEz), 1, map);

        // a cell as "(x,z) F<floor> g(x,z)" (F1 = ground)
        private static string FzCell(IntVec3 c)
        {
            Map map = fzMap;
            if (!c.InBounds(map))
            {
                return $"({c.x},{c.z}) OUT";
            }
            int k = ProjectZ.FloorOf(c, map);
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            return k < 0 ? $"({c.x},{c.z}) VOID" : $"({c.x},{c.z}) F{k + 1} g({g.x},{g.z})";
        }

        // ---- recording hooks (test only)
        public static void FzFire_Prefix(IntVec3 c)
        {
            if (fzRec)
            {
                fzFires.Add(c);
            }
        }

        public static void FzDamage_Prefix(Thing __instance, DamageInfo dinfo)
        {
            if (!fzRec || !__instance.Spawned || __instance is Mote || dinfo.Def == null || dinfo.Def == DamageDefOf.Deterioration || dinfo.Def.defName == "Rotting" || fzThrownNow && dinfo.Instigator != fzShooter)
            {
                return;
            }
            fzHitCells.Add(__instance.Position);
            if (fzDamage.Count < 60)
            {
                fzDamage.Add($"{__instance.def.defName}{(__instance is Pawn ? "(" + __instance.LabelShort + ")" : "")} at {FzCell(__instance.Position)} {dinfo.Def.defName} {dinfo.Amount:0.0}");
            }
        }

        public static void FzActivate_Prefix(Ability __instance)
        {
            if (fzRec && fzAppliedAt < 0)
            {
                fzAppliedAt = Find.TickManager.TicksGame;
            }
        }

        public static void FzBeamHit_Prefix(IntVec3 cell)
        {
            if (fzRec)
            {
                fzBeamShots++;
                fzBeamHits.Add(cell);
            }
        }

        public static void FzLaunch_Prefix(Projectile __instance, Thing launcher, Vector3 origin, LocalTargetInfo usedTarget)
        {
            if (fzRec && launcher == fzShooter)
            {
                fzLaunchFrom.Add(origin.ToIntVec3());
                fzLaunchDest.Add(usedTarget.Cell);
                if (fzAppliedAt < 0)
                {
                    fzAppliedAt = Find.TickManager.TicksGame;
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
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.aimfloor");
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(FireUtility)).Where(m => m.Name == "TryStartFireIn"))
            {
                h.Patch(mi, prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzFire_Prefix)));
            }
            h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzDamage_Prefix)));
            h.Patch(AccessTools.Method(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzActivate_Prefix)));
            h.Patch(AccessTools.Method(typeof(Verb_ShootBeam), "HitCell"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzBeamHit_Prefix)));
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzLaunch_Prefix)));
            h.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzBlast_Prefix)));
            h.Patch(AccessTools.Method(typeof(Projectile), "Tick"), postfix: new HarmonyMethod(typeof(TestRunner), nameof(FzProjTick_Postfix)));
        }

        private static bool StepFlame(Map map)
        {
            switch (stage)
            {
                case "fz_setup":
                    FQuiet(map, true);   // (no stray pawns in the field, no new ones)
                    Seeded("aimfloor_setup", () => SetupFlame(map));
                    return true;
                case "fz_case":
                    if (fzCase >= fzCases.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", 0f);
                        Info($"aimfloor: AimFloor scopes {AimFloor.Scopes}, sight checks {AimFloor.Checks}, sinks moved back {AimFloor.SinksMoved}; rows: " + string.Join(", ", AimFloor.Rows.Select(r => $"{r.Name} {(r.found == null ? "MISSING" : r.swaps.ToString())}")));
                        Result("aimfloor: every listed method patched with swaps", AimFloor.Rows.All(r => r.found != null && r.swaps > 0), string.Join(", ", AimFloor.Rows.Where(r => r.found == null || r.swaps == 0).Select(r => r.Name)));
                        Finish();
                        return true;
                    }
                    FzStart(map, fzCases[fzCase]);
                    return true;
                case "fz_cast":
                {
                    tickWatch = null;
                    FlameCase c = fzCases[fzCase];
                    Info($"{c.name}: cast seen {(fzAppliedAt >= 0 || fzBeamShots > 0 ? "yes" : "NO")}, beam hits {fzBeamShots}, launches {fzLaunchDest.Count}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (c.thrown && fzShooter is Pawn thrower)
                    {
                        thrower.jobs.StopAll();   // one throw (the attack order would throw again)
                    }
                    fzFrame = 0;
                    Wait(1, "fz_yaw");
                    return true;
                }
                case "fz_yaw":
                {
                    IsoRimBridge.SetOption("freeYaw", 0f);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    IntVec3 mid = fzShooter == null ? new IntVec3(fzCx, 0, fzEz + 3) : new IntVec3((ProjectZ.GroundCellOf(fzShooter.Position, map).x + ProjectZ.GroundCellOf(fzH.Position, map).x) / 2, 0, (ProjectZ.GroundCellOf(fzShooter.Position, map).z + ProjectZ.GroundCellOf(fzH.Position, map).z) / 2);
                    if (fzFrame == 1) { ShotAt(map, 1, new IntVec3(fzCx, 0, fzEz + 2), 16f); } else { ShotAt(map, 1, mid, 7f); }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "fz_take");
                    return true;
                }
                case "fz_take":
                {
                    FlameCase c = fzCases[fzCase];
                    ZCapture($"aim_{c.name}_{(fzFrame == 0 ? "close" : "wide")}");
                    if (fzFrame == 0)
                    {
                        FzLogMotes(map, c);
                    }
                    fzFrame++;
                    Wait(3, fzFrame < (fzCases[fzCase].Cross ? 2 : 1) ? "fz_yaw" : "fz_after");
                    return true;
                }
                case "fz_after":
                    // let the effect finish (the burst ends, fires catch, damage lands)
                    SeedTicks("aimfloor_after_" + fzCases[fzCase].name);
                    StartTicks(300, "fz_report");
                    return true;
                case "fz_report":
                    fzRec = false;
                    FzReport(map, fzCases[fzCase]);
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
            fzFires.Clear();
            fzDamage.Clear();
            fzHitCells.Clear();
            fzBeamHits.Clear();
            fzLaunchDest.Clear();
            fzLaunchFrom.Clear();
            fzCone = null;
            fzShooter = null;
            fzAppliedAt = -1;
            fzBeamShots = 0;
            fzBlasts.Clear();
            fzArc.Clear();
            fzProj = null;
            fzThrownNow = c.thrown;
            // the game's own random stream from here on, from this case's seed (set as Rand.Seed would, without its
            // "initial seed" error: a test fixture)
            AccessTools.Field(typeof(Rand), "seed").SetValue(null, StableHash("aimfloor_" + c.name) ^ (uint)Math.Max(0, Seed));
            AccessTools.Field(typeof(Rand), "iterations").SetValue(null, 0u);
            // A stands at the tower's edge only when it shoots (else parked far off, out of every blast)
            Teleport(fzA, c.shooter == 'A' ? FzEdge(map) : new IntVec3(fzCx - 8, 0, fzEz + 20));
            Teleport(fzB, c.shooter == 'B' ? new IntVec3(fzCx + c.bdx, 0, fzEz + c.bdz) : new IntVec3(fzCx - 6, 0, fzEz + 12));
            IntVec3 tg = new IntVec3(fzCx + c.tdx, 0, fzEz + c.tdz);
            if (c.Canopy)
            {
                FzCanopy(map, c, tg);
            }
            IntVec3 tc = c.tk == 0 ? tg : ProjectZ.CellOnFloor(tg, c.tk, map);
            Teleport(fzH, tc);
            fzH.stances.stunner.StunFor(100000, null, addBattleLog: false);
            fzH.Drawer?.tweener?.ResetTweenedPosToRoot();
            foreach (Pawn p in new[] { fzA, fzB })
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
            }
            if (c.turret != null)
            {
                IntVec3 tgc = new IntVec3(fzCx + c.bdx, 0, fzEz + c.bdz);
                ThingDef td = DefDatabase<ThingDef>.GetNamed(c.turret);
                fzTurret = (Building)ThingMaker.MakeThing(td, GenStuff.DefaultStuffFor(td));
                fzTurret.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(fzTurret, c.sk == 0 ? tgc : ProjectZ.CellOnFloor(tgc, c.sk, map), map);
                Teleport(fzA, ProjectZ.CellOnFloor(new IntVec3(fzCx + 2, 0, fzEz - 3), 1, map));
            }
            fzShooter = c.shooter == 'A' ? fzA : c.shooter == 'B' ? (Thing)fzB : fzTurret;
            FloorView.Set(1);
            LocalTargetInfo t = new LocalTargetInfo(fzH);
            IntVec3 sp = fzShooter.Position;
            IntVec3 sg = ProjectZ.GroundCellOf(sp, map);
            float trueAngle = (tg - sg).AngleFlat;
            bool seen = !c.Cross || Los3D.CanSee(sp, tc, map);
            Info($"---- {c.name}: shooter {fzShooter.LabelShort} at {FzCell(sp)} -> raider {FzCell(tc)}; true ground angle {trueAngle:0} deg, 3D distance {Range3D.Distance(sp, tc, map):0.0}, target cell seen (Los3D) {seen}");
            fzScopes0 = AimFloor.Scopes;
            fzRec = true;
            if (c.ability != null)
            {
                AbilityDef ad = DefDatabase<AbilityDef>.GetNamedSilentFail(c.ability);
                if (ad == null)
                {
                    Info($"{c.name}: ability {c.ability} missing (DLC?), skipped");
                    fzRec = false;
                    fzShooter = null;
                    Wait(1, "fz_report");
                    return;
                }
                Pawn s = (Pawn)fzShooter;
                if (s.abilities.GetAbility(ad) == null)
                {
                    s.abilities.GainAbility(ad);
                }
                Ability ab = s.abilities.GetAbility(ad);
                ab.ResetCooldown();
                // the cells the player sees while aiming (the comp's preview, AffectedCells) for the cone/spray comps
                foreach (CompAbilityEffect comp in ab.EffectComps)
                {
                    var mi = AccessTools.Method(comp.GetType(), "AffectedCells", new[] { typeof(LocalTargetInfo) });
                    if (mi != null)
                    {
                        fzCone = new List<IntVec3>((IEnumerable<IntVec3>)mi.Invoke(comp, new object[] { t }));
                        Info($"{c.name} {comp.GetType().Name}.AffectedCells: {fzCone.Count} cells: {string.Join(" ", fzCone.Take(14).Select(FzCell))}");
                    }
                }
                Info($"{c.name}: CanHitTarget {ab.verb.CanHitTarget(t)}, CanCast {ab.CanCast.Accepted}");
                ab.QueueCastingJob(t, t);
                Info($"{c.name}: cast job {(s.CurJob?.ability == ab ? "taken" : "NOT taken")} ({s.CurJobDef?.defName})");
            }
            else if (c.gun != null)
            {
                ThingDef gd = DefDatabase<ThingDef>.GetNamedSilentFail(c.gun);
                if (gd == null)
                {
                    Info($"{c.name}: weapon {c.gun} missing (DLC?), skipped");
                    fzRec = false;
                    fzShooter = null;
                    Wait(1, "fz_report");
                    return;
                }
                Pawn s = (Pawn)fzShooter;
                s.equipment.DestroyAllEquipment();
                s.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gd));
                Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, t);
                j.playerForced = true;
                bool ok = s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                Info($"{c.name}: {c.gun} AttackStatic order {ok}; CanHitTarget {s.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(t)}");
            }
            else
            {
                Verb v = ((Building_TurretGun)fzTurret).AttackVerb;
                // powered and pressed (its interactable), so it ticks its verb through the burst
                CompPowerTrader pw = fzTurret.GetComp<CompPowerTrader>();
                if (pw != null)
                {
                    pw.PowerOn = true;
                }
                Traverse.Create(fzTurret).Field("burstActivated").SetValue(true);
                bool ok = v.TryStartCastOn(t);
                Info($"{c.name}: turret verb {v.GetType().Name} started {ok}; CanHitTarget {v.CanHitTarget(t)}");
            }
            // the screenshot: a beam in mid sweep, a thrown explosive half way, the rest just after the cast
            bool beam = c.gun == "Gun_Incinerator" || c.gun == "Gun_BeamGraser";
            SeedTicks("aimfloor_cast_" + c.name);   // (the cast's ticks seeded tick by tick)
            TickUntil(600, () => beam ? fzBeamShots >= 3 : c.thrown ? FzMidFlight() : fzAppliedAt >= 0 && Find.TickManager.TicksGame - fzAppliedAt >= 2, "fz_cast", 1);
        }

        // A canopy: a floor-2 slab (the roof under it, as a built floor) over the open-air columns where A's sight lines
        // (eye to each aim height) toward the twin case's burned ground cells pass the floor-2 plane, so those cells are
        // hidden from A now; the columns where the lines to the target pass it stay open (the target stays seen).
        private static void FzCanopy(Map map, FlameCase c, IntVec3 tg)
        {
            if (!fzLandedBy.TryGetValue(c.twin, out List<IntVec3> twin))
            {
                return;
            }
            IntVec3 ga = ProjectZ.GroundCellOf(FzEdge(map), map);
            float s = IsoRimBridge.StoreyHeight, eye = s + Los3D.Eye;
            float[] aims = { Los3D.AimWaist, Los3D.AimChest, Los3D.AimHead };
            HashSet<IntVec3> Crossings(IntVec3 g)
            {
                HashSet<IntVec3> set = new HashSet<IntVec3>();
                foreach (float aim in aims)
                {
                    float t = (eye - s) / (eye - aim);
                    float x = ga.x + t * (g.x - ga.x), z = ga.z + t * (g.z - ga.z);
                    foreach (int cx in new[] { Mathf.FloorToInt(x), Mathf.CeilToInt(x) })
                    {
                        foreach (int cz in new[] { Mathf.FloorToInt(z), Mathf.CeilToInt(z) })
                        {
                            set.Add(new IntVec3(cx, 0, cz));
                        }
                    }
                }
                return set;
            }
            HashSet<IntVec3> keep = Crossings(tg);
            HashSet<IntVec3> cover = new HashSet<IntVec3>();
            foreach (IntVec3 g in twin.Where(x => ProjectZ.FloorOf(x, map) == 0 && x.DistanceTo(tg) >= 2f))
            {
                cover.UnionWith(Crossings(g));
            }
            foreach (IntVec3 g in cover.Where(x => x.z > fzEz && !keep.Contains(x) && x.InBounds(map)).OrderBy(x => x.x).ThenBy(x => x.z))
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
                fzCanopy.Add(g);
            }
            Info($"{c.name}: canopy over {fzCanopy.Count} columns: {string.Join(" ", fzCanopy.Select(g => $"({g.x - fzCx},{g.z - fzEz})"))}; the target seen from A {Los3D.CanSee(FzEdge(map), tg, map)}");
        }

        private static void FzReport(Map map, FlameCase c)
        {
            if (fzShooter == null)
            {
                return;
            }
            IntVec3 sp = fzShooter.Position;
            IntVec3 sg = ProjectZ.GroundCellOf(sp, map);
            IntVec3 tc = fzH.Position;
            IntVec3 tg = ProjectZ.GroundCellOf(tc, map);
            float trueAngle = (tg - sg).AngleFlat;
            bool seen = !c.Cross || Los3D.CanSee(sp, tc, map);
            List<IntVec3> landed = fzFires.Concat(fzHitCells).Concat(fzBeamHits).Concat(fzLaunchDest).Where(x => x != sp).Distinct().ToList();
            int inj = fzH.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            // hit: hurt or burning; a foam turret's foam on its cell; a beam's sweep at it or next to it (the game's sweep
            // is offset at random)
            bool hit = inj > 0 || fzH.HasAttachment(ThingDefOf.Fire) || fzHitCells.Contains(tc) || c.turret != null && fzLaunchDest.Contains(tc)
                || fzBeamHits.Any(x => x.AdjacentTo8WayOrInside(tc));
            int scopes = AimFloor.Scopes - fzScopes0;
            fzLandedBy[c.name] = landed;
            if (c.thrown)
            {
                FzReportThrown(map, c, landed, sp, tc, scopes);
                return;
            }
            Info($"{c.name}: fires started {fzFires.Count}: {string.Join(" ; ", fzFires.Distinct().Take(20).Select(FzCell))}");
            Info($"{c.name}: damage {fzDamage.Count}: {string.Join(" ; ", fzDamage.Take(16))}");
            if (fzBeamHits.Count > 0)
            {
                Info($"{c.name}: beam hit cells {fzBeamHits.Count}: {string.Join(" ; ", fzBeamHits.Distinct().Take(20).Select(FzCell))}");
            }
            if (fzLaunchDest.Count > 0)
            {
                Info($"{c.name}: launches {fzLaunchDest.Count} from {string.Join(",", fzLaunchFrom.Distinct().Select(FzCell))} to {string.Join(" ; ", fzLaunchDest.Distinct().Take(16).Select(FzCell))}");
            }
            Info($"{c.name}: AimFloor scopes opened {scopes}; raider injuries {inj}, burning {fzH.HasAttachment(ThingDefOf.Fire)}");
            if (!c.Cross)
            {
                Result($"{c.name} (same floor): the game's own code (no AimFloor scope)", scopes == 0, $"scopes {scopes}");
                Result($"{c.name} (same floor): raider hit", hit, $"injuries {inj}");
                Result($"{c.name} (same floor): all on the raider's floor", landed.All(x => ProjectZ.FloorOf(x, map) == c.tk), $"{landed.Count} cells");
                return;
            }
            Result($"{c.name}: AimFloor scope opened", scopes > 0, $"scopes {scopes}");
            Result($"{c.name}: lands", !seen || landed.Count > 0, $"{landed.Count} cells, target seen {seen}");
            List<IntVec3> off = landed.Where(x => !x.InBounds(map) || ProjectZ.FloorOf(x, map) != c.tk).ToList();
            Result($"{c.name}: only on the target's floor", off.Count == 0, off.Count == 0 ? $"{landed.Count} cells on F{c.tk + 1}" : "off: " + string.Join(" ", off.Take(8).Select(FzCell)));
            List<IntVec3> on = landed.Except(off).ToList();
            if (on.Count > 0)
            {
                float d = Toward(map, on, sg, trueAngle, out string cen);
                Result($"{c.name}: toward the target", d <= 35f, $"centroid {cen}, {d:0} deg off the true direction {trueAngle:0}");
            }
            List<IntVec3> hidden = on.Where(x => !Los3D.CanSee(sp, x, map)).ToList();
            Result($"{c.name}: nothing hidden burned", hidden.Count == 0, hidden.Count == 0 ? $"{on.Count} cells all seen" : "hidden: " + string.Join(" ", hidden.Take(8).Select(FzCell)));
            Result($"{c.name}: raider hit as seen", hit == seen, $"hit {hit} (injuries {inj}), seen {seen}");
            if (fzLaunchFrom.Count > 0)
            {
                int sk = ProjectZ.FloorOf(sp, map);
                Result($"{c.name}: projectiles leave from the shooter's floor", fzLaunchFrom.All(x => ProjectZ.FloorOf(x, map) == sk), string.Join(",", fzLaunchFrom.Distinct().Select(FzCell)));
            }
            if (fzCone != null)
            {
                bool floorOk = fzCone.All(x => ProjectZ.FloorOf(x, map) == c.tk);
                bool seenOk = fzCone.All(x => Los3D.CanSee(sp, x, map));
                float d = fzCone.Count > 0 ? Toward(map, fzCone, sg, trueAngle, out string _) : 180f;
                Result($"{c.name}: cone on the target's floor, toward it, all seen", fzCone.Count > 0 && floorOk && seenOk && d <= 35f, $"{fzCone.Count} cells, floor {floorOk}, seen {seenOk}, {d:0} deg off");
            }
            if (c.Canopy)
            {
                // the canopy hides ground cells the twin case (no canopy) burned; none of the hidden ones burn now (cells
                // under its edge that the shooter still sees at a slant may)
                List<IntVec3> hiddenNow = fzLandedBy[c.twin].Where(g => ProjectZ.FloorOf(g, map) == 0 && !Los3D.CanSee(sp, g, map)).ToList();
                List<IntVec3> under = landed.Where(x => ProjectZ.FloorOf(x, map) == 0 && hiddenNow.Contains(x)).ToList();
                int before = fzLandedBy.TryGetValue(c.twin, out List<IntVec3> tw) ? tw.Count(x => ProjectZ.FloorOf(x, map) == 0 && hiddenNow.Contains(x)) : -1;
                Result($"{c.name}: nothing burned under the canopy where hidden (the twin case burned {before} of those cells)", under.Count == 0 && before > 0,
                    $"hidden canopy cells {hiddenNow.Count} of {fzCanopy.Count}, burned now {under.Count}: {string.Join(" ", under.Take(6).Select(FzCell))}");
            }
        }

        // A thrown explosive: it blows up on the target's floor at the target (the throw's own miss radius), everything it
        // does stays on that floor, and its drawn flight runs from the thrower's hand down (or up) to the landing without
        // jumps (IsoRim's height per tick).
        private static void FzReportThrown(Map map, FlameCase c, List<IntVec3> landed, IntVec3 sp, IntVec3 tc, int scopes)
        {
            IntVec3 tg = ProjectZ.GroundCellOf(tc, map);
            Info($"{c.name}: blasts {fzBlasts.Count}: {string.Join(" ; ", fzBlasts.Select(FzCell))}; fires {fzFires.Count}, damage {fzDamage.Count}: {string.Join(" ; ", fzDamage.Take(10))}");
            string arcLog = string.Join(" ", fzArc.Where((v, i) => i % 4 == 0 || i == fzArc.Count - 1).Select(v => $"({v.x:0.0},{v.z:0.0})h{v.y:0.00}"));
            Info($"{c.name}: flight {fzArc.Count} ticks: {arcLog}");
            Result($"{c.name}: no AimFloor scope (a thrown explosive flies on its own)", scopes == 0, $"scopes {scopes}");
            bool onFloor = fzBlasts.Count > 0 && fzBlasts.All(b => b.InBounds(map) && ProjectZ.FloorOf(b, map) == c.tk);
            float dist = fzBlasts.Count > 0 ? fzBlasts.Max(b => ProjectZ.GroundCellOf(b, map).DistanceTo(tg)) : 99f;
            Result($"{c.name}: explodes on the target's floor at the target", onFloor && dist <= 3f, $"{fzBlasts.Count} blast(s), {dist:0.0} cells from the target over the ground");
            List<IntVec3> off = landed.Where(x => !x.InBounds(map) || ProjectZ.FloorOf(x, map) != c.tk).ToList();
            Result($"{c.name}: effects only on the target's floor", off.Count == 0, off.Count == 0 ? $"{landed.Count} cells on F{c.tk + 1}" : "off: " + string.Join(" ", off.Take(8).Select(FzCell)));
            if (fzArc.Count < 3)
            {
                Result($"{c.name}: a sane arc", false, $"{fzArc.Count} flight samples");
                return;
            }
            int ks = ProjectZ.FloorOf(sp, map);
            float h0 = fzArc[0].y, hn = fzArc[fzArc.Count - 1].y, top = fzArc.Max(v => v.y);
            float step = 0f, back = 0f;
            Vector3 g0 = new Vector3(ProjectZ.GroundCellOf(sp, map).x + 0.5f, 0f, ProjectZ.GroundCellOf(sp, map).z + 0.5f);
            // "moves away from the thrower": progress along the throw (its first sample toward its last), so a launch point
            // off the thrower's cell centre (the hand) passing by that centre is not counted as a step back
            Vector2 dir = new Vector2(fzArc[fzArc.Count - 1].x - fzArc[0].x, fzArc[fzArc.Count - 1].z - fzArc[0].z);
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.zero;
            for (int i = 1; i < fzArc.Count; i++)
            {
                step = Mathf.Max(step, Mathf.Abs(fzArc[i].y - fzArc[i - 1].y));
                float along = Vector2.Dot(new Vector2(fzArc[i].x - fzArc[i - 1].x, fzArc[i].z - fzArc[i - 1].z), dir);
                back = Mathf.Max(back, -along);
            }
            float startD = new Vector2(fzArc[0].x - g0.x, fzArc[0].z - g0.z).magnitude;
            Vector3 last = fzArc[fzArc.Count - 1];
            float endD = new Vector2(last.x - (tg.x + 0.5f), last.z - (tg.z + 0.5f)).magnitude;
            // starts at the thrower (over its floor, within 1.5 cells), ends over the target's floor near the target, moves
            // away from the thrower every tick, never jumps in height, and arcs no higher than 3 cells over its ends
            bool ok = startD <= 1.5f && h0 >= ks * IsoRimBridge.StoreyHeight - 0.1f && h0 <= ks * IsoRimBridge.StoreyHeight + 2f
                && hn >= c.tk * IsoRimBridge.StoreyHeight - 0.1f && hn <= c.tk * IsoRimBridge.StoreyHeight + 1.5f && endD <= 3.5f
                && step <= 0.6f && back <= 0.05f && top <= Mathf.Max(h0, hn) + 3f;
            Result($"{c.name}: a sane arc", ok, $"start {startD:0.0} from the thrower at h{h0:0.00} (floor {ks * IsoRimBridge.StoreyHeight:0.00}), end {endD:0.0} from the target at h{hn:0.00} (floor {c.tk * IsoRimBridge.StoreyHeight:0.00}), top h{top:0.00}, max step {step:0.00}, max step back {back:0.00}");
        }

        // What IsoRim draws between the floors at the screenshot (its FloorSprays counters, the dual motes' placement).
        private static void FzLogMotes(Map map, FlameCase c)
        {
            Type fs = AccessTools.TypeByName("IsoRim.FloorSprays");
            if (fs != null)
            {
                Info($"{c.name}: IsoRim FloorSprays: sprays {Traverse.Create(fs).Field("Sprays").GetValue()}, duals {Traverse.Create(fs).Field("Duals").GetValue()}, streams {Traverse.Create(fs).Field("Streams").GetValue()}, tilts {Traverse.Create(fs).Field("Tilts").GetValue()}");
                // the last mote drawn between floors: where its two ends show on screen, against the shooter's feet and
                // the target's (the 3D view's own projection)
                if (c.Cross && fzShooter != null && Traverse.Create(fs).Field("Tilts").GetValue<int>() > 0)
                {
                    Matrix4x4 m = Traverse.Create(fs).Field("LastTilt").GetValue<Matrix4x4>();
                    float sq = Traverse.Create(AccessTools.TypeByName("IsoRim.Api")).Property("HeightSquash").GetValue<float>();
                    Vector3 True(Vector3 p) => new Vector3(p.x, p.y * sq, p.z);
                    Vector2 e0 = IsoRimBridge.ScreenOf(True(m.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f))));
                    Vector2 e1 = IsoRimBridge.ScreenOf(True(m.MultiplyPoint3x4(new Vector3(0f, 0f, 0.5f))));
                    float st = IsoRimBridge.StoreyHeight;
                    IntVec3 gs = ProjectZ.GroundCellOf(fzShooter.Position, map), gt = ProjectZ.GroundCellOf(fzH.Position, map);
                    Vector2 ss = IsoRimBridge.ScreenOf(new Vector3(gs.x + 0.5f, ProjectZ.FloorOf(fzShooter.Position, map) * st, gs.z + 0.5f));
                    Vector2 ts = IsoRimBridge.ScreenOf(new Vector3(gt.x + 0.5f, c.tk * st, gt.z + 0.5f));
                    Info($"{c.name}: last tilted mote ends on screen ({e0.x:0},{e0.y:0}) and ({e1.x:0},{e1.y:0}); shooter's feet ({ss.x:0},{ss.y:0}), target's ({ts.x:0},{ts.y:0}); columns {m.GetColumn(0)} {m.GetColumn(2)} {m.GetColumn(3)}");
                }
            }
            List<MoteDualAttached> motes = new List<MoteDualAttached>();
            if ((fzShooter as Pawn)?.equipment?.PrimaryEq?.PrimaryVerb is Verb_ShootBeam vb && Traverse.Create(vb).Field("mote").GetValue() is MoteDualAttached bm)
            {
                motes.Add(bm);
            }
            foreach (Thing sp in map.listerThings.AllThings.Where(x => x.def == ThingDefOf.IncineratorSpray))
            {
                if (Traverse.Create(sp).Field("projectiles").GetValue() is List<IncineratorProjectileMotion> ms)
                {
                    motes.AddRange(ms.Select(x => x.mote).Where(x => x != null && x.Spawned).Take(3));
                }
            }
            foreach (MoteDualAttached m in motes.Take(6))
            {
                MoteAttachLink l2 = Traverse.Create(m).Field("link2").GetValue<MoteAttachLink>();
                Info($"{c.name} mote {m.def.defName} at {m.Position} exact ({m.exactPosition.x:0.0},{m.exactPosition.y:0.00},{m.exactPosition.z:0.0}) rot {m.exactRotation:0} scale ({m.linearScale.x:0.0},{m.linearScale.z:0.0}) link1 {(m.link1.Linked ? m.link1.LastDrawPos.ToString() : "-")} link2 {(l2.Linked ? l2.LastDrawPos.ToString() : "-")}");
            }
        }

        private static float Toward(Map map, List<IntVec3> cells, IntVec3 sg, float trueAngle, out string cen)
        {
            List<IntVec3> gs = cells.Select(x => ProjectZ.GroundCellOf(x, map)).Where(x => x.IsValid).ToList();
            Vector3 mean = new Vector3(gs.Average(x => x.x + 0.5f), 0f, gs.Average(x => x.z + 0.5f));
            float a = (mean - sg.ToVector3Shifted()).AngleFlat();
            cen = $"g({mean.x:0.0},{mean.z:0.0}) at {a:0} deg";
            return Mathf.Abs(Mathf.DeltaAngle(a, trueAngle));
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
            foreach (IntVec3 g in fzCanopy)
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(g, null);
            }
            fzCanopy.Clear();
            // the tower's slab laid again where a blast broke it (each case starts on the same fixture)
            foreach (IntVec3 g in fzTower)
            {
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                if (u.GetTerrain(map) != PZDefOf.PZ_UpperFloor)
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                }
            }
            if (fzTurret != null && fzTurret.Spawned)
            {
                fzTurret.Destroy();
            }
            fzTurret = null;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                t.Destroy();
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.IncineratorSpray || t is Projectile).ToList())
            {
                t.Destroy();
            }
            foreach (Pawn p in new[] { fzA, fzB })
            {
                if (p?.equipment?.PrimaryEq != null)
                {
                    p.equipment.PrimaryEq.PrimaryVerb.Reset();
                }
                p?.stances?.CancelBusyStanceHard();
            }
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
                Result("aimfloor setup", false, $"floors {an?.floors}, colonists {cols.Count}");
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
                Result("aimfloor field", false, "map too small");
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
            fzTower = new CellRect(fzCx - 3, found.minZ, 7, 7);
            fzEz = fzTower.maxZ;
            foreach (IntVec3 g in fzTower)
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
            Info($"aimfloor: field {found}, tower {fzTower} (slab on floor 2), edge row {fzEz}, groundZ {an.groundZ}, stride {an.stride}, floors {an.floors}; A {fzA}, B {fzB}, raider {fzH} ({f}); seed {Seed}");
            Info($"DLCs: Biotech {ModsConfig.BiotechActive}, Anomaly {ModsConfig.AnomalyActive}, Odyssey {ModsConfig.OdysseyActive}");
            fzCase = 0;
            Wait(10, "fz_case");
        }
    }
}
