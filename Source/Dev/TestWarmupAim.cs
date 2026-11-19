using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode warmupaim (PR 8, Combat/WarmupAim.cs; run with -projectzfloors=3 -projectzseed=7, Biotech, Anomaly and Odyssey;
    // -projectzfloors=1: the map without floors). The aim indicators of a warm-up (Stance_Warmup: the aim line, the
    // charge mote in front of the muzzle, the target mote) between floors. Every case is its own fixture (the area wiped
    // on all levels, the level-1 platform laid again, a fresh shooter) under its own seed: a real pawn gets a real order
    // (a Diabolus its AttackStatic job with the hellsphere cannon, a colonist the fleshmelter bolt's cast job or an
    // AttackStatic with a beam repeater; once the Diabolus picks its target itself, in a DefendPoint lord), the game
    // ticks until the warm-up has run a few ticks, and the motes the game's own StanceTick placed are read. Nothing of
    // ProjectZ's patch is called by the test, and the file names nothing of it but the two patch names (a string each),
    // so the same file builds into the build before the patch (base vs head on one harness). The warm-up is stopped
    // before the shot.
    //   WX  between floors: the direction (read off the charge mote: caster cell + direction x offset) is the bearing
    //       over the ground (ProjectZ.Virtual of both ends), the charge and target motes are turned to it, the target
    //       mote sits on the target, the aim line's far end is the target's centre on the target's floor
    //   WZ  the target straight above the caster (no ground distance): everything finite, the direction zero, no error
    //   WS  one floor (the ground, and both on level 1): every value the game's own formula gives, to the bit
    //   WN  the map without floors (-projectzfloors=1): the same, with the target many rows north
    //   WP  (head) both patches applied, the copied method watched with its hash as registered
    // "WAV" lines carry every value read at full precision: the one-floor and no-floors lines of a base and a head run
    // are the same text. Screenshots of the marked cases at that moment of the warm-up (3D view, level 1 shown).
    public static partial class TestRunner
    {
        private sealed class WaCase
        {
            public string name, shooter;   // shooter: diabolus (ordered), ai (a Diabolus that picks its target), bolt, repeater
            public int sx, sz, sL;         // the shooter's ground cell in the area, its level
            public int tx, tz, tL;         // the target's
            public bool wall;              // the target is a wall there (a thing), else the cell
            public bool shot;
        }

        private static readonly WaCase[] waCases =
        {
            // between floors
            new WaCase { name = "hell_up", shooter = "diabolus", sx = 10, sz = 10, tx = 21, tz = 14, tL = 1, shot = true },
            new WaCase { name = "hell_down", shooter = "diabolus", sx = 22, sz = 15, sL = 1, tx = 11, tz = 9, shot = true },
            new WaCase { name = "hell_ai_up", shooter = "ai", sx = 10, sz = 10, tx = 21, tz = 15, tL = 1 },
            new WaCase { name = "bolt_up_wall", shooter = "bolt", sx = 10, sz = 10, tx = 21, tz = 16, tL = 1, wall = true },
            new WaCase { name = "bolt_down_wall", shooter = "bolt", sx = 22, sz = 15, sL = 1, tx = 12, tz = 18, wall = true },
            new WaCase { name = "repeater_up_wall", shooter = "repeater", sx = 10, sz = 10, tx = 21, tz = 16, tL = 1, wall = true },
            new WaCase { name = "bolt_above", shooter = "bolt", sx = 10, sz = 24, tx = 10, tz = 24, tL = 1 },
            // one floor
            new WaCase { name = "hell_same_ground", shooter = "diabolus", sx = 10, sz = 10, tx = 19, tz = 16, shot = true },
            new WaCase { name = "hell_same_upper", shooter = "diabolus", sx = 21, sz = 14, sL = 1, tx = 31, tz = 18, tL = 1 },
            new WaCase { name = "hell_north", shooter = "diabolus", sx = 10, sz = 4, tx = 12, tz = 20 },
            new WaCase { name = "bolt_same_wall", shooter = "bolt", sx = 10, sz = 10, tx = 17, tz = 5, wall = true },
            new WaCase { name = "repeater_same_wall", shooter = "repeater", sx = 10, sz = 10, tx = 17, tz = 5, wall = true },
        };

        // The level-1 platform (over these ground cells of the area), laid for every case of a map with floors.
        private static CellRect WaPlatform => new CellRect(r2Area.minX + 20, r2Area.minZ + 12, 13, 9);

        // Warm-up ticks run before the motes are read (StanceTick has placed them that often).
        private const int WaWarmTicks = 12;

        private static int waI, waErr0, waWarm;
        private static Pawn waShooter;
        private static LocalTargetInfo waTarget;
        private static bool waFloors;

        private static bool StepWarmupAim(Map map)
        {
            switch (stage)
            {
                case "wa_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    NoRoofCollapse(map);
                    Find.Storyteller.storytellerComps.Clear();
                    NoAmbushes(map);
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    foreach (IntVec3 o in GenRadial.RadialPattern.Take(400))
                    {
                        CellRect r = new CellRect(map.Center.x - 22 + o.x * 6, map.Center.z - 16 + o.z * 6, 44, 32);
                        if (r.ExpandedBy(3).InBounds(map) && r.ExpandedBy(3).maxZ < ProjectZ.GroundZ(map) && !r.ExpandedBy(3).Cells.Any(c => c.GetThingList(map).Any(t => !t.def.destroyable)))
                        {
                            r2Area = r;
                            break;
                        }
                    }
                    var cols = Colonists(map);
                    r2P = cols.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.WorkTagIsDisabled(WorkTags.Violent)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    waFloors = ProjectZ.HasAnnex(map) && Levels.Max(map) >= 1;
                    SetIsoRimOption("freeYaw", 0f);
                    Info($"warmupaim: area {r2Area}, map {map.Size} with floors {waFloors} (levels {Levels.Min(map)}..{Levels.Max(map)}), caster {r2P}, IsoRim {IsoRimBridge.Present} (Api {IsoRimBridge.ApiVersion}), seed {FixtureSeedBase}");
                    bool aim = FailSoft.IsApplied("Stance_Warmup_AimDir_Patch"), end = FailSoft.IsApplied("Stance_Warmup_TargetPos_Patch");
                    string hashes = CopyWatches.Hashes();
                    // (the game's method as IsoRim hashes it in this build, patched or not: the same text in a base and a head run)
                    Info("warmupaim: Stance_Warmup.AimDir hashes as " + (IsoRimBridge.Present && IsoRimBridge.ApiVersion >= CopyWatches.NeedApi ? WaAimDirHash() : "(IsoRim Api " + IsoRimBridge.ApiVersion + ": no copy watch)"));
                    Result("WP both warm-up patches applied; every copied method watched, its hash as registered",
                        aim && end && CopyWatches.Registered == CopyWatches.Count && hashes.Contains("Stance_Warmup.AimDir") && !hashes.Contains("(registered") && !hashes.Contains("(not found)"),
                        $"Stance_Warmup_AimDir_Patch {aim}, Stance_Warmup_TargetPos_Patch {end}; watches registered {CopyWatches.Registered}/{CopyWatches.Count}; hashes {hashes}");
                    waI = 0;
                    WaNextCase();
                    return true;
                }
                case "wa_fixture":
                    // (this case's own random state)
                    Rand.PushState(FixtureSeed("warmupaim_" + waCases[waI].name));
                    try
                    {
                        WaFixture(map);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    return true;
                case "wa_order":
                    Rand.PushState(FixtureSeed("warmupaim_order_" + waCases[waI].name));
                    try
                    {
                        WaOrder(map);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    return true;
                case "wa_check":
                    WaCheck(map);
                    return true;
                case "wa_shot":
                    Capture("wa_" + waCases[waI].name);
                    Wait(3, "wa_next");
                    return true;
                case "wa_next":
                    WaStop();
                    waI++;
                    WaNextCase();
                    return true;
                case "wa_end":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.UIRoot.screenshotMode.Active = false;
                    RestoreIsoRimOption("freeYaw");
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string WaAimDirHash() => IsoRim.Api.CopyHash(AccessTools.Method(typeof(Stance_Warmup), "AimDir"));

        // The next case this map can hold (without floors: the cases on the ground only).
        private static void WaNextCase()
        {
            while (waI < waCases.Length && !waFloors && (waCases[waI].sL != 0 || waCases[waI].tL != 0))
            {
                waI++;
            }
            Wait(3, waI < waCases.Length ? "wa_fixture" : "wa_end");
        }

        private static IntVec3 WaCell(int x, int z, int L, Map map) => L == 0 ? RG(x, z) : RL(x, z, L, map);

        private static void WaFixture(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.UIRoot.screenshotMode.Active = false;
            WaCase c = waCases[waI];
            R2Clear(map);
            R2Feed(map);
            if (waFloors)
            {
                R2Floor(map, WaPlatform, 1);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 at = WaCell(c.sx, c.sz, c.sL, map);
            IntVec3 to = WaCell(c.tx, c.tz, c.tL, map);
            waTarget = c.wall ? new LocalTargetInfo(Place(ThingDefOf.Wall, ThingDefOf.WoodLog, to, map)) : new LocalTargetInfo(to);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            if (c.shooter == "diabolus" || c.shooter == "ai")
            {
                R2Park(map, c.shooter == "ai" ? new[] { r2P } : new Pawn[0]);
                waShooter = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mech_Diabolus"), Faction.OfMechanoids, forceGenerateNewPawn: true));
                GenSpawn.Spawn(waShooter, at, map);
                if (c.shooter == "ai")
                {
                    // the colonist it finds: on the target cell, drafted, holding its fire
                    Teleport(r2P, to);
                    r2P.equipment.DestroyAllEquipment();
                    Draft(r2P, true);
                }
            }
            else
            {
                R2Park(map, r2P);
                waShooter = r2P;
                Teleport(r2P, at);
                r2P.equipment.DestroyAllEquipment();
                if (c.shooter == "repeater")
                {
                    r2P.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BeamRepeater")));
                }
                else
                {
                    r2P.abilities.GainAbility(DefDatabase<AbilityDef>.GetNamed("FleshmelterBolt"));
                }
                Draft(r2P, true);
            }
            if (c.shot)
            {
                IntVec3 gs = RG(c.sx, c.sz), gt = RG(c.tx, c.tz);
                ShotAt(map, waFloors ? 1 : 0, new IntVec3((gs.x + gt.x) / 2, 0, (gs.z + gt.z) / 2), 9f);
            }
            // (a few seeded ticks: the shooter drawn where it stands)
            SeedTicks("warmupaim_settle_" + c.name);
            StartTicks(3, "wa_order");
        }

        private static void WaOrder(Map map)
        {
            WaCase c = waCases[waI];
            Job job = null;
            if (c.shooter == "diabolus")
            {
                job = JobMaker.MakeJob(JobDefOf.AttackStatic, waTarget);
                waShooter.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            else if (c.shooter == "ai")
            {
                LordMaker.MakeNewLord(Faction.OfMechanoids, new LordJob_DefendPoint(waShooter.Position, 0f, 2f, addFleeToil: false), map, new[] { waShooter });
            }
            else if (c.shooter == "repeater")
            {
                job = JobMaker.MakeJob(JobDefOf.AttackStatic, waTarget);
                job.playerForced = true;
                waShooter.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            }
            else
            {
                Ability ab = waShooter.abilities.GetAbility(DefDatabase<AbilityDef>.GetNamed("FleshmelterBolt"));
                job = ab?.GetJob(waTarget, waTarget);
                if (job != null)
                {
                    job.playerForced = true;
                    waShooter.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                }
            }
            Info($"WA {c.name}: {waShooter} ({waShooter.Faction?.Name}) at {waShooter.Position} (level {Levels.LevelOf(waShooter.Position, map)}) with {waShooter.equipment?.Primary?.def.defName ?? "-"}, target {waTarget} at {waTarget.Cell} (level {Levels.LevelOf(waTarget.Cell, map)}), job {job?.def.defName ?? (c.shooter == "ai" ? "(its own: a DefendPoint lord)" : "none")}");
            waErr0 = errors;
            waWarm = 0;
            SeedTicks("warmupaim_ticks_" + c.name);
            TickUntil(c.shooter == "ai" ? 1500 : 400, () =>
            {
                if (waShooter.stances.curStance is Stance_Warmup w && w.verb != null && (w.verb.verbProps.aimingChargeMote != null || w.verb.verbProps.aimingLineMote != null))
                {
                    waWarm++;
                }
                return waWarm >= WaWarmTicks;
            }, "wa_check", 1);
        }

        private static string WaF(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        private static string WaV(Vector3 v) => $"({WaF(v.x)}, {WaF(v.z)})";

        private static bool WaFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        private static bool WaNear(Vector3 a, Vector3 b, float tol) => Mathf.Abs(a.x - b.x) <= tol && Mathf.Abs(a.z - b.z) <= tol;

        private static bool WaSame(Vector3 a, Vector3 b) => a.x == b.x && a.z == b.z;

        private static void WaCheck(Map map)
        {
            tickWatch = null;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            WaCase c = waCases[waI];
            string code = !waFloors ? "WN" : c.sL == c.tL ? "WS" : c.sx == c.tx && c.sz == c.tz ? "WZ" : "WX";
            Stance_Warmup w = waShooter.stances.curStance as Stance_Warmup;
            if (w == null || w.verb == null || waWarm < WaWarmTicks)
            {
                Result($"{code} {c.name}: the shooter warms up", false, $"stance {waShooter.stances.curStance?.GetType().Name}, warm-up ticks seen {waWarm}, job {waShooter.CurJobDef?.defName}, at {waShooter.Position}, errors {errors - waErr0}");
                Wait(3, "wa_next");
                return;
            }
            VerbProperties vp = w.verb.verbProps;
            LocalTargetInfo focus = w.focusTarg;
            int ks = Levels.LevelOf(waShooter.Position, map), kt = Levels.LevelOf(focus.Cell, map);
            bool cross = waFloors && ks != kt;
            Vector3 from = waShooter.DrawPos, to = focus.CenterVector3;
            // the game's own formula (Verse.Stance_Warmup.AimDir), the reference on one floor
            Vector3 game = to - from;
            game.y = 0f;
            game.Normalize();
            // the bearing over the ground: both ends through THE map of positions between floors
            Vector3 ground = ProjectZ.Virtual(to, map) - ProjectZ.Virtual(from, map);
            ground.y = 0f;
            ground.Normalize();
            Vector3 want = cross ? ground : game;
            float wantAngle = want.AngleFlat();

            Mote line = Traverse.Create(w).Field("aimLineMote").GetValue<Mote>();
            Mote charge = Traverse.Create(w).Field("aimChargeMote").GetValue<Mote>();
            Mote tmote = Traverse.Create(w).Field("aimTargetMote").GetValue<Mote>();
            List<string> bad = new List<string>();
            List<string> vals = new List<string> { $"caster drawn {WaV(from)} cell {waShooter.Position} level {ks}", $"target {WaV(to)} cell {focus.Cell} level {kt}", $"want {WaV(want)} angle {WaF(wantAngle)}" };
            if (vp.aimingChargeMote != null != (charge != null) || vp.aimingLineMote != null != (line != null) || vp.aimingTargetMote != null != (tmote != null))
            {
                bad.Add($"motes missing (line {line != null}, charge {charge != null}, target {tmote != null})");
            }
            Vector3 cell = waShooter.Position.ToVector3Shifted();
            if (charge != null)
            {
                // (StanceTick: caster cell + direction x offset, turned by the direction's angle)
                Vector3 wantPos = cell + want * vp.aimingChargeMoteOffset;
                Vector3 seen = (charge.exactPosition - cell) / vp.aimingChargeMoteOffset;
                seen.y = 0f;
                vals.Add($"charge at {WaV(charge.exactPosition)} rot {WaF(charge.exactRotation)} (direction read {WaV(seen)})");
                if (!WaFinite(charge.exactPosition) || float.IsNaN(charge.exactRotation))
                {
                    bad.Add("charge mote not finite");
                }
                if (cross ? !WaNear(charge.exactPosition, wantPos, 0.0001f) : !WaSame(charge.exactPosition, wantPos))
                {
                    bad.Add($"charge mote at {WaV(charge.exactPosition)}, wanted {WaV(wantPos)}");
                }
                if (cross ? Mathf.Abs(Mathf.DeltaAngle(charge.exactRotation, wantAngle)) > 0.01f : charge.exactRotation != wantAngle)
                {
                    bad.Add($"charge mote turned {WaF(charge.exactRotation)}, wanted {WaF(wantAngle)}");
                }
            }
            if (tmote != null)
            {
                vals.Add($"target mote at {WaV(tmote.exactPosition)} rot {WaF(tmote.exactRotation)}");
                if (!WaFinite(tmote.exactPosition) || float.IsNaN(tmote.exactRotation))
                {
                    bad.Add("target mote not finite");
                }
                if (!WaSame(tmote.exactPosition, to))
                {
                    bad.Add($"target mote at {WaV(tmote.exactPosition)}, the target is at {WaV(to)}");
                }
                if (cross ? Mathf.Abs(Mathf.DeltaAngle(tmote.exactRotation, wantAngle)) > 0.01f : tmote.exactRotation != wantAngle)
                {
                    bad.Add($"target mote turned {WaF(tmote.exactRotation)}, wanted {WaF(wantAngle)}");
                }
            }
            if (line != null)
            {
                // the line's far end as StanceTick linked it: a cell and an offset from its centre
                MoteAttachLink far = Traverse.Create(line).Field("link2").GetValue<MoteAttachLink>();
                IntVec3 farCell = far.Target.Cell;
                Vector3 farOff = far.GetOffset();
                Vector3 farEnd = farCell.ToVector3Shifted() + farOff;
                int kf = Levels.LevelOf(farCell, map);
                vals.Add($"line far end {WaV(farEnd)} (cell {farCell} level {(kf == Levels.None ? "void" : kf.ToString())}, offset {WaV(farOff)})");
                if (!WaFinite(farEnd))
                {
                    bad.Add("line end not finite");
                }
                if (cross)
                {
                    // between floors: the target itself, on the target's floor
                    if (!WaNear(farEnd, to, 0.0001f) || farCell != focus.Cell || kf != kt)
                    {
                        bad.Add($"line far end {WaV(farEnd)} (cell {farCell}, level {(kf == Levels.None ? "void" : kf.ToString())}), the target is at {WaV(to)} (cell {focus.Cell}, level {kt})");
                    }
                }
                else
                {
                    // one floor: the game's TargetPos (a fixed length from the caster along the direction, else the target)
                    Vector3 wantEnd = vp.aimingLineMoteFixedLength.HasValue ? from + game * vp.aimingLineMoteFixedLength.Value : to;
                    IntVec3 wantCell = wantEnd.ToIntVec3();
                    Vector3 wantOff = wantEnd - wantCell.ToVector3Shifted();
                    if (farCell != wantCell || !WaSame(farOff, wantOff))
                    {
                        bad.Add($"line far end cell {farCell} offset {WaV(farOff)}, the game's formula gives cell {wantCell} offset {WaV(wantOff)}");
                    }
                }
            }
            if (errors != waErr0)
            {
                bad.Add($"{errors - waErr0} errors logged");
            }
            float off = Vector3.Angle(game, ground);
            string what = code == "WX" ? "between floors: the motes follow the bearing over the ground, the aim line ends on the target"
                : code == "WZ" ? "the target straight above: no direction, everything finite, the aim line ends on the target"
                : code == "WS" ? "one floor: every value the game's own, to the bit"
                : "no floors: every value the game's own, to the bit";
            Def source = (Def)w.verb.EquipmentSource?.def ?? (w.verb as IAbilityVerb)?.Ability?.def;
            Info($"WAV {c.name}: verb {w.verb.GetType().Name} ({source?.defName}), fixed line {(vp.aimingLineMoteFixedLength.HasValue ? WaF(vp.aimingLineMoteFixedLength.Value) : "-")}, offset {WaF(vp.aimingChargeMoteOffset)}; " + string.Join("; ", vals));
            Result($"{code} {c.name}: {what}", bad.Count == 0,
                $"{(bad.Count == 0 ? "as wanted" : string.Join("; ", bad))} [warm-up {w.ticksLeft} ticks left, floors {ks} -> {kt}, ground distance {(ProjectZ.Virtual(to, map) - ProjectZ.Virtual(from, map)).MagnitudeHorizontal():0.00}, the game's row direction is {off:0.0} deg off the ground bearing]");
            if (c.shot)
            {
                Find.UIRoot.screenshotMode.Active = true;
                Wait(120, "wa_shot");   // (the camera settles over some frames)
                return;
            }
            Wait(3, "wa_next");
        }

        // The warm-up stopped before the shot; the shooter of this case gone (a Diabolus) or as it was (the colonist).
        private static void WaStop()
        {
            Find.UIRoot.screenshotMode.Active = false;
            if (waShooter != null && !waShooter.Destroyed)
            {
                waShooter.GetLord()?.Notify_PawnLost(waShooter, PawnLostCondition.Vanished);
                waShooter.jobs?.StopAll();
                waShooter.stances?.CancelBusyStanceHard();
                if (waShooter != r2P)
                {
                    waShooter.Destroy();
                }
            }
            AbilityDef bolt = DefDatabase<AbilityDef>.GetNamedSilentFail("FleshmelterBolt");
            if (bolt != null && r2P?.abilities?.GetAbility(bolt) != null)
            {
                r2P.abilities.RemoveAbility(bolt);
            }
            r2P?.equipment?.DestroyAllEquipment();
            waShooter = null;
        }
    }
}
