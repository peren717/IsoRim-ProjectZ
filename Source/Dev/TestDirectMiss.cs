using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode directmiss (run with -projectzfloors=3 -projectzseed=7): a direct shot from an upper floor that misses over open
    // air lands on the floor below at impact and never on the shooter's own floor (Projectile_Launch_Patch keeps a direct
    // shot on its floor, Projectile_ImpactDirect_Patch drops it where it comes to rest, Column.ShotLanding). A cleared field;
    // a 5x14 slab on floor 2 (FloorOf 1) with a drafted colonist at its south end; a real AttackStatic order with a triple
    // rocket launcher (a direct rocket, the game's forced miss of 2.9 cells) at a floor-2 cell over open air 9 rows north
    // of the slab (the shooter 12 rows in: a line from its muzzle down to the ground under the miss would cut the slab, the
    // old bug: the rocket blew up on its own floor; the misses stay out of the blasts' 3D reach of the slab). The fire
    // ticks are seeded (SeedTicks). Every explosion of the shooter's rockets is recorded (a test prefix on
    // GenExplosion.DoExplosion). Checks:
    //   DM1 the burst's rockets exploded (the def's burstShotCount);
    //   DM2 each one on the ground, in a column with open air on floor 2, within the forced-miss radius of the aimed cell
    //       (it flew on its floor and dropped there: none on floor 2, none at the shooter);
    //   DM3 the shooter's slab is intact (every cell still a slab, no slab damaged or broken) and the shooter unhurt (no
    //       more injuries than at setup).
    public static partial class TestRunner
    {
        private static Harmony dmHarmony;
        private static Pawn dmShooter;
        private static CellRect dmSlab;
        private static IntVec3 dmAim;
        private static int dmDamaged, dmBroken, dmFireTicks, dmBurst, dmInjuries;
        private static ThingDef dmGun;
        private static readonly List<IntVec3> dmBlasts = new List<IntVec3>();

        private const string DmGunName = "Gun_TripleRocket";   // a direct rocket with a forced miss (its burst: dmBurst, the def's)
        private const int DmAimRows = 9;        // the aimed cell's rows north of the slab
        private const int DmSlabRows = 14;      // the slab's rows; the shooter stands on its second row
        private const float DmMissSlack = 1.5f; // past the forced-miss radius: a cell's corner

        private static bool StepDirectMiss(Map map)
        {
            switch (stage)
            {
                case "dm_setup":
                    SetupDirectMiss(map);
                    return true;
                case "dm_fire":
                {
                    ThingDef gun = dmGun;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;   // (only the seeded ticks below run)
                    Rand.PushState(FixtureSeed("dm_order"));
                    dmShooter.equipment.DestroyAllEquipment();
                    dmShooter.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
                    Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, new LocalTargetInfo(dmAim));
                    j.playerForced = true;
                    bool ok = dmShooter.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                    Rand.PopState();
                    Info($"directmiss: {dmShooter} at {dmShooter.Position} (floor {ProjectZ.FloorOf(dmShooter.Position, map) + 1}) with {gun.defName} -> {dmAim} (floor {ProjectZ.FloorOf(dmAim, map) + 1}, open air {!ProjectZ.HasFloor(dmAim, map)}): order {ok}");
                    SeedTicks("dm_fire");
                    TickUntil(1200, () => dmBlasts.Count >= dmBurst, "dm_settle", 1);
                    return true;
                }
                case "dm_settle":
                    dmFireTicks = ticksTotal - ticksLeft;
                    // (the last blast's fire and debris settle; the shooter's slab is checked after them)
                    SeedTicks("dm_settle");
                    StartTicks(120, "dm_check");
                    return true;
                case "dm_check":
                    CheckDirectMiss(map);
                    dmHarmony?.UnpatchAll(dmHarmony.Id);
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupDirectMiss(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).OrderBy(p => p.thingIDNumber).ToList();
            if (an == null || an.floors < 2 || cols.Count == 0)
            {
                Result("directmiss setup", false, $"needs floors (-projectzfloors=3) and a colonist able to shoot; floors {an?.floors}, colonists {cols.Count}");
                Finish();
                return;
            }
            // The fixture is the same on any map (the generated map is not the same run to run, even with the seed): a 15x30
            // field at a fixed place (the first, in a fixed order from the map's south middle, with nothing indestructible
            // in it), made bare soil without roofs, things or animals; the setup's random picks under a fixed state.
            Rand.PushState(FixtureSeed("dm_setup"));
            CellRect found = CellRect.Empty;
            int x0 = map.Size.x / 2 - 7;
            for (int i = 0; i < 40 && found.Area == 0; i++)
            {
                int x = x0 + (i % 2 == 0 ? 1 : -1) * (i + 1) / 2 * 17;
                CellRect r = new CellRect(x, 3, 15, 30);
                if (r.minX >= 3 && r.maxX < map.Size.x - 3 && r.maxZ < an.groundZ - 3
                    && !r.ExpandedBy(1).Cells.Any(c => c.GetThingList(map).Any(t => !t.def.destroyable)))
                {
                    found = r;
                }
            }
            if (found.Area == 0)
            {
                Rand.PopState();
                Result("directmiss field", false, "no field without indestructible things");
                Finish();
                return;
            }
            IntVec3 away = new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5);
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is Pawn p)
                    {
                        Teleport(p, CellFinder.RandomClosewalkCellNear(away, map, 6));
                    }
                    else if (!t.Destroyed)
                    {
                        t.Destroy();
                    }
                }
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                IntVec3 up = ProjectZ.CellOnFloor(c, 1, map);
                if (up.IsValid)
                {
                    map.fogGrid.Unfog(up);
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int cx = found.CenterCell.x;
            dmSlab = new CellRect(cx - 2, found.minZ + 2, 5, DmSlabRows);
            foreach (IntVec3 g in dmSlab)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
            }
            dmAim = ProjectZ.CellOnFloor(new IntVec3(cx, 0, dmSlab.maxZ + DmAimRows), 1, map);
            dmShooter = cols[0];
            // everyone else drafted, holding fire, far from the field
            foreach (Pawn p in Colonists(map).Where(p => p != dmShooter))
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                Teleport(p, CellFinder.RandomClosewalkCellNear(away, map, 4));
            }
            Rand.PopState();
            Teleport(dmShooter, ProjectZ.CellOnFloor(new IntVec3(cx, 0, dmSlab.minZ + 1), 1, map));
            dmShooter.drafter.Drafted = true;
            dmShooter.drafter.FireAtWill = false;
            dmGun = DefDatabase<ThingDef>.GetNamed(DmGunName);
            dmBurst = dmGun.Verbs[0].burstShotCount;
            dmInjuries = DmInjuries();
            dmDamaged = SlabDamage.Damaged;
            dmBroken = SlabDamage.Broken;
            dmBlasts.Clear();
            dmHarmony = dmHarmony ?? new Harmony("Peren.IsoRimProjectZ.test.directmiss");
            dmHarmony.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(DmExplosion)));
            Info($"directmiss: field {found}, slab {dmSlab} on floor 2, shooter {dmShooter}, aimed cell {dmAim} (ground {ProjectZ.GroundCellOf(dmAim, map)})");
            Wait(10, "dm_fire");
        }

        private static int DmInjuries() => dmShooter.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);

        public static void DmExplosion(IntVec3 center, Thing instigator)
        {
            if (instigator != null && instigator == dmShooter)
            {
                dmBlasts.Add(center);
            }
        }

        private static void CheckDirectMiss(Map map)
        {
            Result("DM1 the triple rocket's burst exploded", dmBlasts.Count == dmBurst, $"{dmBlasts.Count} blasts of {dmBurst} within {dmFireTicks} ticks of the order");
            IntVec3 aimG = ProjectZ.GroundCellOf(dmAim, map);
            float reach = dmGun.Verbs[0].ForcedMissRadius + DmMissSlack;
            List<string> where = new List<string>();
            bool landed = dmBlasts.Count > 0;
            foreach (IntVec3 b in dmBlasts)
            {
                IntVec3 g = ProjectZ.GroundCellOf(b, map);
                IntVec3 above = ProjectZ.CellOnFloor(g, 1, map);
                bool ok = ProjectZ.FloorOf(b, map) == 0 && above.IsValid && !ProjectZ.HasFloor(above, map) && g.DistanceTo(aimG) <= reach;
                landed &= ok;
                where.Add($"{b} (floor {ProjectZ.FloorOf(b, map) + 1}, {g.DistanceTo(aimG):0.0} from the aimed column{(ok ? "" : ", WRONG")})");
            }
            Result("DM2 each rocket missed over open air on floor 2 and landed on the ground below, near its aimed column", landed, string.Join("; ", where));
            int slabs = dmSlab.Cells.Count(g => SlabDamage.SlabAt(ProjectZ.CellOnFloor(g, 1, map), map) != null);
            bool intact = slabs == dmSlab.Area && SlabDamage.Damaged == dmDamaged && SlabDamage.Broken == dmBroken;
            bool unhurt = !dmShooter.Dead && DmInjuries() == dmInjuries;
            Result("DM3 the shooter's own slab is intact and the shooter unhurt", intact && unhurt,
                $"slab cells {slabs}/{dmSlab.Area}, slabs damaged {SlabDamage.Damaged - dmDamaged}, broken {SlabDamage.Broken - dmBroken}; shooter dead {dmShooter.Dead}, injuries {DmInjuries()} (at setup {dmInjuries})");
        }
    }
}
