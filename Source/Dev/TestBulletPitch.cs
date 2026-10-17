using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode bulletpitch (projectiles between floors drawn pitched along their 3D flight, ProjectilePitch; run with
    // -projectzfloors=3). A 7x7 tower with slabs on floors 2 and 3 at the south of a cleared field. Colonist A stands on
    // floor 3 at the tower's north edge, B and D on the ground north of it. Every shot is a real ordered attack
    // (AttackStatic, or a manned mortar's forced target) fired on real ticks; the ticks stop while the projectile is
    // in the middle of its flight, and it is shot frozen at yaw 45 and 225 (bullets: a second frame 2 ticks later):
    //   bullet down (A -> D), bullet up (B -> A), bullet on one floor (B -> D, control: drawn exactly as before),
    //   arrow down, frag grenade down (thrown, arc), doomsday rocket down, incendiary launcher (arc) down, mortar shell
    //   from floor 3 down. The pitch each was drawn with is logged against the pitch expected from the heights
    //   (shooter's floor + muzzle to the target's floor + aim height, over the flat flight).
    public static partial class TestRunner
    {
        private sealed class PitchCase
        {
            public string name;
            public string gun;
            public char shooter;   // 'A' floor 3, 'B' ground
            public char target;    // 'A', 'D' (pawns), 'c' a ground cell, 'm' mortar (A mans it, a far ground cell)
            public bool burst;
            public int fromFloor, toFloor;
        }

        private static readonly PitchCase[] iCases =
        {
            new PitchCase { name = "bullet_down", gun = "Gun_AssaultRifle", shooter = 'A', target = 'D', burst = true, fromFloor = 2, toFloor = 0 },
            new PitchCase { name = "bullet_up", gun = "Gun_AssaultRifle", shooter = 'B', target = 'A', burst = true, fromFloor = 0, toFloor = 2 },
            new PitchCase { name = "bullet_flat", gun = "Gun_AssaultRifle", shooter = 'B', target = 'D', burst = true, fromFloor = 0, toFloor = 0 },
            new PitchCase { name = "arrow_down", gun = "Bow_Recurve", shooter = 'A', target = 'D', fromFloor = 2, toFloor = 0 },
            new PitchCase { name = "grenade_down", gun = "Weapon_GrenadeFrag", shooter = 'A', target = 'c', fromFloor = 2, toFloor = 0 },
            new PitchCase { name = "rocket_down", gun = "Gun_DoomsdayRocket", shooter = 'A', target = 'c', fromFloor = 2, toFloor = 0 },
            new PitchCase { name = "launcher_down", gun = "Gun_IncendiaryLauncher", shooter = 'A', target = 'c', fromFloor = 2, toFloor = 0 },
            new PitchCase { name = "mortar_down", gun = null, shooter = 'A', target = 'm', fromFloor = 2, toFloor = 0 },
        };

        private static Pawn iA, iB, iD;
        private static int icx, iEz, iCase;
        private static CellRect iTower;
        private static Projectile iProj;
        private static Building_TurretGun iMortar;
        private static float iOldYaw, iOldMin;
        private static int iFrame;
        private static bool iOrdered;

        private static IntVec3 IEdge(Map map) => ProjectZ.CellOnFloor(new IntVec3(icx, 0, iEz), 2, map);

        private static bool StepBulletPitch(Map map)
        {
            switch (stage)
            {
                case "i_setup":
                    SetupBulletPitch(map);
                    return true;
                case "i_case":
                {
                    if (iCase >= iCases.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", iOldYaw);
                        Find.CameraDriver.config.sizeRange.min = iOldMin;
                        Info($"pitch draws: tilted {ProjectilePitch.tiltedDraws}, flat {ProjectilePitch.flatDraws}; cross-floor shots {PZCombatStats.crossFloorShots}");
                        WatchCheck(map, "bulletpitch end");
                        Finish();
                        return true;
                    }
                    StartCase(map, iCases[iCase]);
                    return true;
                }
                case "i_frozen":
                {
                    tickWatch = null;
                    PitchCase c = iCases[iCase];
                    if (iProj == null)
                    {
                        Result($"{c.name}: a projectile mid-flight", false, $"none within {ticksTotal} ticks; shooter job {Shooter(c)?.CurJobDef?.defName}, stance {Shooter(c)?.stances?.curStance}");
                        EndCase(map);
                        return true;
                    }
                    iFrame = 0;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(1, "i_yaw");
                    return true;
                }
                case "i_yaw":
                {
                    // frames: 0 yaw 45, 1 yaw 45 drawn the old way (no pitch; shots between floors only), 2 yaw 225,
                    // then for bursts 2 ticks on: 3 yaw 45
                    PitchCase c = iCases[iCase];
                    if (!iProj.Spawned)
                    {
                        Info($"{c.name}: projectile gone before frame {iFrame}");
                        EndCase(map);
                        return true;
                    }
                    ProjectilePitch.Disabled = iFrame == 1;
                    IsoRimBridge.SetOption("freeYaw", iFrame == 2 ? 180f : 0f);
                    IntVec3 g = ProjectZ.GroundCellOf(iProj.Position, map);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    // (a mortar shell flies its arc high over the ground cell under it: framed wide enough to show it)
                    ShotAt(map, 2, g, c.target == 'm' ? 14f : c.name == "rocket_down" ? 7f : 4.5f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "i_take");
                    return true;
                }
                case "i_take":
                {
                    PitchCase c = iCases[iCase];
                    string yaw = iFrame == 2 ? "yaw225" : "yaw45";
                    string shot = $"pitch_{c.name}_{yaw}{(iFrame == 1 ? "_old" : iFrame == 3 ? "_b" : "")}";
                    ZCapture(shot);
                    Wait(3, "i_logged");
                    return true;
                }
                case "i_logged":
                {
                    PitchCase c = iCases[iCase];
                    LogPitch(map, c, iFrame);
                    ProjectilePitch.Disabled = false;
                    iFrame++;
                    if (iFrame == 1 && c.fromFloor == c.toFloor)
                    {
                        iFrame = 2;
                    }
                    if (iFrame == 3 && c.burst)
                    {
                        StartTicks(2, "i_yaw");
                        return true;
                    }
                    if (iFrame < 3)
                    {
                        Wait(1, "i_yaw");
                        return true;
                    }
                    EndCase(map);
                    return true;
                }
                case "i_settle":
                    HealAll(map);
                    iCase++;
                    Wait(2, "i_case");
                    return true;
            }
            return false;
        }

        private static Pawn Shooter(PitchCase c) => c.shooter == 'A' ? iA : iB;

        private static void StartCase(Map map, PitchCase c)
        {
            foreach (Pawn p in new[] { iA, iB, iD })
            {
                p.jobs.StopAll();
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
            }
            Teleport(iA, IEdge(map));
            Teleport(iB, new IntVec3(icx - 3, 0, iEz + 8));
            Teleport(iD, new IntVec3(icx + 3, 0, iEz + 10));
            // fired while the ground is viewed (floors 2 and 3 hidden: the shooter upstairs is not drawn), as in play
            FloorView.Set(0);
            Pawn s = Shooter(c);
            iProj = null;
            iOrdered = false;
            bool ok;
            if (c.target == 'm')
            {
                // the mortar on floor 3 in the tower's middle, manned by A, at a ground cell 34 north
                IntVec3 at = ProjectZ.CellOnFloor(new IntVec3(icx, 0, iEz - 2), 2, map);
                iMortar = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), null, at, map);
                iMortar.gun.TryGetComp<CompChangeableProjectile>()?.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"), 4);
                Teleport(iA, at + IntVec3.South);
                ok = iA.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, iMortar), JobTag.DraftedOrder);
                Info($"{c.name}: mortar {iMortar} at {at} (floor 3) manned by {iA}: order {ok}, forced target {iMortar.ForcedTarget}");
            }
            else
            {
                s.equipment.DestroyAllEquipment();
                s.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(c.gun)));
                LocalTargetInfo t = c.target == 'A' ? new LocalTargetInfo(iA) : c.target == 'D' ? new LocalTargetInfo(iD) : new LocalTargetInfo(new IntVec3(icx + 4, 0, iEz + (c.name == "rocket_down" ? 20 : 12)));
                Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, t);
                j.playerForced = true;
                ok = s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                Info($"{c.name}: {s} at {s.Position} (floor {ProjectZ.FloorOf(s.Position, map) + 1}) with {c.gun} -> {t} (floor {ProjectZ.FloorOf(t.Cell, map) + 1}): order {ok}");
            }
            Pawn shooter = s;
            TickUntil(c.target == 'm' ? 3000 : 900, () =>
            {
                if (c.target == 'm' && !iOrdered && iMortar.TryGetComp<CompMannable>()?.MannedNow == true)
                {
                    iOrdered = true;
                    iMortar.OrderAttack(new LocalTargetInfo(new IntVec3(icx, 0, iEz + 34)));
                    Info($"{c.name}: mortar manned after {ticksTotal - ticksLeft} ticks, forced target {iMortar.ForcedTarget}");
                }
                iProj = MidFlightOf(map, c.target == 'm' ? (Thing)iMortar : shooter, shooter, c.fromFloor != c.toFloor);
                return iProj != null;
            }, "i_frozen", 1);
        }

        // A projectile from this shooter between 30 and 60 % of its flat way, between floors or not (a miss up that comes
        // down on open air falls back onto the shooter's floor: skipped).
        private static Projectile MidFlightOf(Map map, Thing from, Pawn launcher, bool cross)
        {
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (!(t is Projectile p) || p.Launcher != launcher && p.Launcher != from || cross != Flights.cross.TryGetValue(p, out _))
                {
                    continue;
                }
                Vector3 o = projOrigin(p), d = projDest(p);
                float total = (d - o).MagnitudeHorizontal();
                float f = total > 0.1f ? (p.ExactPosition - o).MagnitudeHorizontal() / total : 0f;
                if (f >= 0.3f && f <= 0.6f)
                {
                    return p;
                }
            }
            return null;
        }

        // A map point (x, z in any floor's rows) as a true 3D point over the ground: x, height, ground z.
        private static Vector3 OverGround(Vector3 at, float height, Map map)
        {
            IntVec3 c = at.ToIntVec3();
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            return new Vector3(at.x, height, at.z - (g.IsValid ? c.z - g.z : 0));
        }

        private static void LogPitch(Map map, PitchCase c, int frame)
        {
            Projectile p = iProj;
            Vector3 o = projOrigin(p), d = projDest(p);
            float flat = (d - o).MagnitudeHorizontal();
            float f = flat > 0.1f ? (p.ExactPosition - o).MagnitudeHorizontal() / flat : 0f;
            bool cross = Flights.cross.TryGetValue(p, out Flights.Cross x);
            int ko = cross ? x.from : ProjectZ.FloorOf(p.Position, map);
            int kd = cross ? x.to : ko;
            float storey = IsoRimBridge.StoreyHeight;
            // The expected start: the muzzle of the real shooter (a manned mortar: its barrel), from where the game
            // launched the shot (the shooter's middle), toward the destination over the ground.
            Thing owner = c.target == 'm' ? (Thing)iMortar : Shooter(c);
            Vector3 dg = OverGround(d, kd * storey + IsoRimBridge.AimHeight(p), map);
            Vector3 og = OverGround(owner.DrawPos, 0f, map);
            Vector3 dir = new Vector3(dg.x - og.x, 0f, dg.z - og.z).normalized;
            Vector3 m = IsoRimBridge.Muzzle(owner, p.EquipmentDef, owner.DrawPos, dir);
            Vector3 muzzle = OverGround(m, ko * storey + m.y, map);
            Vector3? ds = IsoRimBridge.DrawnStart(p);
            Vector3 start = ds.HasValue ? OverGround(ds.Value, ds.Value.y, map) : OverGround(o, IsoRimBridge.ProjectileHeight(p, o), map);
            float startOff = (start - muzzle).magnitude;
            float expected = cross ? Mathf.Atan2(dg.y - muzzle.y, new Vector2(dg.x - muzzle.x, dg.z - muzzle.z).magnitude) * Mathf.Rad2Deg : 0f;
            bool drawn = ProjectilePitch.drawnPitch.TryGetValue(p.thingIDNumber, out float pitch);
            // IsoRim's own drawn flight: its start to the destination (what the tilt follows)
            float hD = IsoRimBridge.ProjectileHeight(p, d);
            Vector3 dd = OverGround(d, hD, map);
            float drawnSlope = cross ? Mathf.Atan2(hD - start.y, new Vector2(dd.x - start.x, dd.z - start.z).magnitude) * Mathf.Rad2Deg : 0f;
            Vector3 ex = p.ExactPosition;
            Vector2 px = IsoRimBridge.ScreenOf(OverGround(ex, IsoRimBridge.ProjectileHeight(p, ex), map));
            string detail = $"screen px ({px.x:0}, {Screen.height - px.y:0}) of {Screen.width}x{Screen.height}; {p.def.defName} floor {ko + 1} -> {kd + 1}, {f:P0} of {flat:0.0} cells; "
                + $"drawn start {start.x:0.00},{start.y:0.00},{start.z:0.00} vs {(c.target == 'm' ? "barrel" : "muzzle")} of {owner} {muzzle.x:0.00},{muzzle.y:0.00},{muzzle.z:0.00} (off {startOff:0.000}; game launch point {og.x:0.00},{og.z:0.00}); "
                + $"drawn pitch {(drawn ? pitch.ToString("0.0") : "not drawn")} deg, expected {expected:0.0}, IsoRim's drawn slope {drawnSlope:0.0}; arc {p.def.projectile.arcHeightFactor}, spin {p.def.projectile.spinRate}, overhead {p.def.projectile.flyOverhead}";
            if (frame > 0)
            {
                Info($"{c.name} frame {frame}: {detail}");
                return;
            }
            if (c.fromFloor == c.toFloor)
            {
                Result($"{c.name}: on one floor, drawn flat as before (no tilt)", drawn && !cross && pitch == 0f, detail);
                return;
            }
            Result($"{c.name}: drawn leaving from the real {(c.target == 'm' ? "barrel" : "muzzle")} (within 0.05 cells)", startOff < 0.05f, detail);
            if (p.def.projectile.flyOverhead)
            {
                // A lobbed shell (mortar, artillery) is drawn by IsoRim itself on its arc up into the sky and down onto the
                // target, nose along the arc; it never asks for a pitched card (ProjectilePitch only tilts flat flights), so
                // no pitch is recorded for it: that is the expected drawing, not a missing one.
                Result($"{c.name}: drawn on its lobbed arc, not pitched as a straight flight", !drawn, detail);
                return;
            }
            bool sign = c.toFloor < c.fromFloor ? pitch < -1f : pitch > 1f;
            Result($"{c.name}: drawn pitched {(c.toFloor < c.fromFloor ? "down" : "up")} along its 3D flight (within 1 deg of expected)", drawn && cross && sign && Mathf.Abs(pitch - expected) < 1f && Mathf.Abs(pitch - drawnSlope) < 0.2f, detail);
        }

        private static void EndCase(Map map)
        {
            foreach (Pawn p in new[] { iA, iB, iD })
            {
                p.jobs.StopAll();
            }
            if (iMortar != null && iMortar.Spawned)
            {
                iMortar.OrderAttack(LocalTargetInfo.Invalid);
                iMortar.Destroy();
            }
            iMortar = null;
            StartTicks(400, "i_settle");
        }

        private static void HealAll(Map map)
        {
            foreach (Pawn p in new[] { iA, iB, iD })
            {
                if (p.Dead)
                {
                    ResurrectionUtility.TryResurrect(p);
                }
                HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(p);
                foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
                {
                    p.health.RemoveHediff(h);
                }
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                t.Destroy();
            }
        }

        private static void SetupBulletPitch(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            // (the scenario's colonists are random: add colonists who can shoot until there are enough)
            for (int i = 0; i < 3 && (cols.Count < 2 || Colonists(map).Count < 3); i++)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(extra, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
                Info($"added colonist {extra} (the scenario's could not all shoot)");
                cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            }
            if (an == null || an.floors < 3 || cols.Count < 2 || Colonists(map).Count < 3)
            {
                Result("bulletpitch setup", false, $"needs 3 floors (-projectzfloors=3), 3 colonists, 2 of them able to shoot; floors {an?.floors}, colonists {cols.Count}");
                Finish();
                return;
            }
            // a 21x44 open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 44 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 21 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 21, 44);
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
                Result("bulletpitch field", false, "map too small");
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
            icx = found.CenterCell.x;
            iTower = new CellRect(icx - 3, found.minZ, 7, 7);
            iEz = iTower.maxZ;
            for (int k = 1; k < 3; k++)
            {
                foreach (IntVec3 g in iTower)
                {
                    map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, k - 1, map), RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, k, map), PZDefOf.PZ_UpperFloor);
                    map.fogGrid.Unfog(ProjectZ.CellOnFloor(g, k, map));
                }
            }
            cols = cols.OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0).ToList();
            iA = cols[0];
            iB = cols[1];
            iD = Colonists(map).First(p => p != iA && p != iB);
            // everyone else drafted, holding fire, out of the field
            foreach (Pawn p in Colonists(map).Where(p => p != iA && p != iB && p != iD))
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                IntVec3 away = CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4);
                Teleport(p, away);
            }
            Info($"field {found} (centre x {icx}), tower {iTower} (slabs on floors 2 and 3), edge row {iEz}; storey {IsoRimBridge.StoreyHeight:0.00}, squash {IsoRimBridge.Squash:0.000}; A {iA}, B {iB}, D {iD}");
            iOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            iOldMin = Find.CameraDriver.config.sizeRange.min;
            iCase = 0;
            ProjectilePitch.drawnPitch.Clear();
            Wait(10, "i_case");
        }
    }
}
