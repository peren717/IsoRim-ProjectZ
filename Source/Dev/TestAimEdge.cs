using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode aimedge (run with -projectzfloors=2 -projectzseed=7): burner streams (CompAbilityEffect_Burner, the
    // IncineratorBurner ability) cast at the map's edges, on one floor and between floors (AimFloor). Found in a pz_main
    // regress run (2026-11-12): a raider at (60,0,1) burning toward the south edge threw IndexOutOfRangeException in
    // GenSight.LastPointOnLineOfSight (the burner's validator CanBeSeenOverFast on a cell past the edge).
    // Each case has its own fixture (a cleared strip, a floor-2 slab where needed, built under its own seed), a colonist
    // with the ability cast by a real order on real seeded ticks at a stunned raider. Recorded per case: the cast, AimFloor
    // scopes opened, errors logged during the cast, the streams' end cells (IncineratorSpray.Add).
    //   same_south     ground (cx,1) -> ground (cx+2,0): the game's own code (no scope); the streams run past the south edge
    //   same_top       floor 2 -> floor 2 toward the top band's north edge: the game's own code; past the map's top row
    //   cross_down     floor 2 (slab's south edge) -> ground at the south edge: AimFloor's walk from the virtual cell
    //   cross_up_top   ground -> floor 2 at the top band's north edge: AimFloor's walk, the line leaves the map
    //   cross_mid      floor 2 -> ground mid-map (control: the streams land toward the raider)
    public static partial class TestRunner
    {
        private sealed class EdgeCase
        {
            public string name;
            public int cx;              // the strip's centre column
            public int sx, sz, sk;      // shooter ground cell and level
            public int tx, tz, tk;      // raider ground cell and level
            public int slabMinZ, slabMaxZ;   // slab rows (ground rows) on level 1, columns cx-3..cx+3; none when slabMinZ < 0
            public bool Cross => sk != tk;
        }

        private static List<EdgeCase> aeCases;
        private static int aeCase, aeErrors0, aeScopes0, aeAppliedAt;
        private static Pawn aeShooter, aeRaider;
        private static bool aeRec, aePatched;
        private static readonly List<IntVec3> aeEnds = new List<IntVec3>();

        // (test only) a burner stream added to its spray: its end cell
        public static void AeSprayAdd_Prefix(IncineratorProjectileMotion proj)
        {
            if (aeRec)
            {
                aeEnds.Add(proj.worldTarget.ToIntVec3());
            }
        }

        public static void AeActivate_Prefix(Ability __instance)
        {
            if (aeRec && aeAppliedAt < 0 && __instance.pawn == aeShooter)
            {
                aeAppliedAt = Find.TickManager.TicksGame;
            }
        }

        private static bool StepAimEdge(Map map)
        {
            switch (stage)
            {
                case "ae_setup":
                    FQuiet(map, true);
                    Seeded("aimedge_setup", () => SetupAimEdge(map));
                    return true;
                case "ae_case":
                    if (aeCase >= aeCases.Count)
                    {
                        Finish();
                        return true;
                    }
                    Rand.PushState(FixtureSeed("aimedge_" + aeCases[aeCase].name));
                    try
                    {
                        AeStart(map, aeCases[aeCase]);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    return true;
                case "ae_cast":
                    tickWatch = null;
                    SeedTicks("aimedge_after_" + aeCases[aeCase].name);
                    StartTicks(60, "ae_report");
                    return true;
                case "ae_report":
                    aeRec = false;
                    AeReport(map, aeCases[aeCase]);
                    aeCase++;
                    Wait(2, "ae_case");
                    return true;
            }
            return false;
        }

        private static void SetupAimEdge(Map map)
        {
            if (!aePatched)
            {
                aePatched = true;
                Harmony h = new Harmony("Peren.IsoRimProjectZ.test.aimedge");
                h.Patch(AccessTools.Method(typeof(IncineratorSpray), nameof(IncineratorSpray.Add)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(AeSprayAdd_Prefix)));
                h.Patch(AccessTools.Method(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(AeActivate_Prefix)));
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            Pawn col = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            if (an == null || an.MaxLevel != 1 || col == null || DefDatabase<AbilityDef>.GetNamedSilentFail("IncineratorBurner") == null)
            {
                Result("aimedge setup", false, $"needs -projectzfloors=2, a violent colonist and Anomaly: top level {an?.MaxLevel}, colonist {col}, Anomaly {ModsConfig.AnomalyActive}");
                Finish();
                return;
            }
            int gz = an.groundZ;
            int w = map.Size.x;
            aeCases = new List<EdgeCase>
            {
                new EdgeCase { name = "same_south", cx = w * 1 / 6, sx = 0, sz = 1, sk = 0, tx = 2, tz = 0, tk = 0, slabMinZ = -1 },
                new EdgeCase { name = "cross_down", cx = w * 2 / 6, sx = 0, sz = 3, sk = 1, tx = 2, tz = 0, tk = 0, slabMinZ = 3, slabMaxZ = 9 },
                new EdgeCase { name = "same_top", cx = w * 3 / 6, sx = 0, sz = gz - 8, sk = 1, tx = 0, tz = gz - 2, tk = 1, slabMinZ = gz - 10, slabMaxZ = gz - 1 },
                new EdgeCase { name = "cross_up_top", cx = w * 4 / 6, sx = 0, sz = gz - 8, sk = 0, tx = 0, tz = gz - 2, tk = 1, slabMinZ = gz - 3, slabMaxZ = gz - 1 },
                new EdgeCase { name = "cross_mid", cx = w * 5 / 6, sx = 0, sz = gz / 2, sk = 1, tx = 3, tz = gz / 2 + 6, tk = 0, slabMinZ = gz / 2 - 6, slabMaxZ = gz / 2 },
            };
            foreach (EdgeCase c in aeCases)
            {
                c.sx += c.cx;
                c.tx += c.cx;
            }
            // the other colonists parked out of every strip
            foreach (Pawn p in Colonists(map).Where(p => p != col))
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                Teleport(p, new IntVec3(w / 12, 0, gz / 2));
            }
            DebugSettings.enableRandomMentalStates = false;
            aeShooter = col;
            Faction f = LEnemy();
            aeRaider = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            aeRaider.equipment.DestroyAllEquipment();
            GenSpawn.Spawn(aeRaider, new IntVec3(w / 12, 0, gz / 2 + 4), map);
            LordMaker.MakeNewLord(f, new LordJob_DefendPoint(aeRaider.Position, 0.9f, 0.9f, false, false), map, new[] { aeRaider });
            Info($"aimedge: map {map.Size}, groundZ {gz}, stride {an.stride}, top level {an.MaxLevel}; shooter {aeShooter}, raider {aeRaider} ({f}); seed {FixtureSeedBase}");
            aeCase = 0;
            Wait(10, "ae_case");
        }

        // A case's fixture: its strip (columns cx-6..cx+6 over the rows it uses, every level) cleared, the slab laid.
        private static void AeFixture(Map map, EdgeCase c)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int z0 = Mathf.Max(0, Mathf.Min(c.sz, c.tz) - 14), z1 = Mathf.Min(an.groundZ - 1, Mathf.Max(c.sz, c.tz) + 14);
            foreach (IntVec3 g in CellRect.FromLimits(c.cx - 6, z0, c.cx + 6, z1))
            {
                if (!g.InBounds(map))
                {
                    continue;
                }
                foreach (int k in new[] { 0, 1 })
                {
                    IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                    foreach (Thing t in u.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && t.def.destroyable)
                        {
                            t.Destroy();
                        }
                    }
                    if (k == 0)
                    {
                        TerrainDef td = g.GetTerrain(map);
                        if (td.IsWater || td.passability == Traversability.Impassable)
                        {
                            map.terrainGrid.SetTerrain(g, TerrainDefOf.Soil);
                        }
                    }
                    else
                    {
                        map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
                    }
                    map.roofGrid.SetRoof(u, null);
                    map.fogGrid.Unfog(u);
                }
            }
            if (c.slabMinZ >= 0)
            {
                foreach (IntVec3 g in CellRect.FromLimits(c.cx - 3, c.slabMinZ, c.cx + 3, c.slabMaxZ))
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
        }

        private static void AeStart(Map map, EdgeCase c)
        {
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                t.Destroy();
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.IncineratorSpray).ToList())
            {
                t.Destroy();
            }
            AeFixture(map, c);
            foreach (Pawn p in new[] { aeShooter, aeRaider })
            {
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
            IntVec3 sc = ProjectZ.CellOnFloor(new IntVec3(c.sx, 0, c.sz), c.sk, map);
            IntVec3 tc = ProjectZ.CellOnFloor(new IntVec3(c.tx, 0, c.tz), c.tk, map);
            Teleport(aeShooter, sc);
            Teleport(aeRaider, tc);
            aeRaider.stances.stunner.StunFor(100000, null, addBattleLog: false);
            foreach (Pawn p in new[] { aeShooter, aeRaider })
            {
                p.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
            aeShooter.drafter.Drafted = true;
            aeShooter.drafter.FireAtWill = false;
            aeEnds.Clear();
            aeAppliedAt = -1;
            aeErrors0 = errors;
            aeScopes0 = AimFloor.Scopes;
            AbilityDef ad = DefDatabase<AbilityDef>.GetNamed("IncineratorBurner");
            if (aeShooter.abilities.GetAbility(ad) == null)
            {
                aeShooter.abilities.GainAbility(ad);
            }
            Ability ab = aeShooter.abilities.GetAbility(ad);
            ab.ResetCooldown();
            LocalTargetInfo lt = new LocalTargetInfo(aeRaider);
            Info($"---- {c.name}: shooter at {sc} (L{c.sk}) -> raider at {tc} (L{c.tk}); cross {c.Cross}; CanHitTarget {ab.verb.CanHitTarget(lt)}, CanCast {ab.CanCast.Accepted}");
            aeRec = true;
            ab.QueueCastingJob(lt, lt);
            Info($"{c.name}: cast job {(aeShooter.CurJob?.ability == ab ? "taken" : "NOT taken")} ({aeShooter.CurJobDef?.defName})");
            SeedTicks("aimedge_cast_" + c.name);
            TickUntil(600, () => aeAppliedAt >= 0 && Find.TickManager.TicksGame - aeAppliedAt >= 2, "ae_cast", 1);
        }

        private static void AeReport(Map map, EdgeCase c)
        {
            int errs = errors - aeErrors0;
            int scopes = AimFloor.Scopes - aeScopes0;
            int outOfMap = aeEnds.Count(x => !x.InBounds(map));
            int k = c.tk;
            int onFloor = aeEnds.Count(x => x.InBounds(map) && ProjectZ.FloorOf(x, map) == k);
            Info($"{c.name}: cast {(aeAppliedAt >= 0 ? "yes" : "NO")}, AimFloor scopes {scopes}, errors during the cast {errs}, streams {aeEnds.Count} (on the raider's floor {onFloor}, off the map {outOfMap}): {string.Join(" ", aeEnds.Take(15))}");
            Result($"aimedge {c.name}: the burner cast", aeAppliedAt >= 0, $"activated at tick {aeAppliedAt}");
            Result($"aimedge {c.name}: {(c.Cross ? "AimFloor scope opened (between floors)" : "the game's own code (no AimFloor scope)")}", c.Cross ? scopes > 0 || AimFloor.Off : scopes == 0, $"scopes {scopes}");
            if (c.Cross)
            {
                Result($"aimedge {c.name}: no error during the cast", errs == 0, $"{errs} errors logged");
            }
            else
            {
                // one floor: the game's own CompAbilityEffect_Burner walk (not ProjectZ's); errors here are the game's
                Info($"aimedge {c.name}: errors during the cast on one floor (the game's own code): {errs}");
            }
        }
    }
}
