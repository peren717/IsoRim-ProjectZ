using System.Collections.Generic;
using System.Linq;
using RimWorld;
using ProjectZ.Combat;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode slabarmor (armoured slabs, user 2026-10-25; run with -projectzfloors=3 -projectzseed=7). A slab of a Metallic
    // stuff with a MaxHitPoints factor of at least 2 (vanilla: plasteel, uranium) takes a heavy hit only from 55 damage,
    // so frag grenades and mortar shells (Bomb 50) leave it alone; its damaged slab keeps 35. Real flow with ticks, every
    // check on its own pavilion (a roofed 7x7 ground with one slab floor above), under its own random state:
    //   G  frag grenades thrown by a drafted colonist (AttackStatic from 2 cells) at the middle of a wood, steel, granite,
    //      plasteel and uranium pavilion, up to 3 each: grenades needed to break it (wood 1, steel 2, granite 2), plasteel
    //      and uranium untouched after 3;
    //   M  a mortar HE shell (the vanilla projectile, launched by a colonist) aimed at the ground under a steel, plasteel
    //      and uranium pavilion: steel damaged, plasteel and uranium untouched;
    //   H  a 60 (plasteel) and 55 (uranium) Bomb explosion on the grenade pavilions: both damaged; then a frag grenade
    //      breaks each damaged slab;
    //   S  a power beam (orbital targeter used by a colonist) over a uranium pavilion: damaged on its first hit, then gone.
    public static partial class TestRunner
    {
        private static readonly string[] saStuffNames = { "WoodLog", "Steel", "BlocksGranite", "Plasteel", "Uranium" };
        private static readonly int[] saGrenadesToBreak = new int[5];
        private static readonly string[] saMortarState = new string[3];
        private static readonly List<string> saBeamSeq = new List<string>();
        private static int saIdx, saThrows, saBeamStart;
        private static Pawn saPawn;
        private static string saRing;

        private const int SaMaxThrows = 3;

        private static ThingDef SaStuff(int i) => DefDatabase<ThingDef>.GetNamed(saStuffNames[i]);

        private static TerrainDef SaSlab(int i) => i == 0 ? PZDefOf.PZ_UpperFloor : SlabDefs.For(SaStuff(i));

        // Row G (grenades, then heavy blasts) z 20..26; row M (mortar: steel, plasteel, uranium) z 40..46; row S z 55..65.
        private static CellRect SaG(int i) => new CellRect(20 + 14 * i, 20, 7, 7);

        private static CellRect SaM(int i) => new CellRect(20 + 14 * i, 40, 7, 7);

        private static readonly CellRect saS = new CellRect(20, 55, 11, 11);

        private static IntVec3 SaMid(CellRect r, Map map) => SxUp(r.CenterCell, 1, map);

        // The armoured rule as specified (independent of SlabDefs).
        private static bool SaArmoredSpec(ThingDef s) => s != null && s.stuffProps?.categories != null && s.stuffProps.categories.Any(c => c?.defName == "Metallic")
            && s.stuffProps.statFactors.GetStatFactorFromList(StatDefOf.MaxHitPoints) >= 2f;

        private static string SaRing(IntVec3 mid, float radius, Map map) => string.Join(",", GenRadial.RadialCellsAround(mid, radius, true).GroupBy(c => SxState(c, map)).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()));

        private static bool StepSlabArmor(Map map)
        {
            Rand.PushState((int)(StableHash("slabarmor_" + stage + "_" + saIdx + "_" + saThrows) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepSlabArmorInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void SaTickUntil(int max, System.Func<bool> cond, string next, int every)
        {
            SeedTicks("slabarmor_" + next + "_" + saIdx + "_" + saThrows);
            TickUntil(max, cond, next, every);
        }

        private static bool StepSlabArmorInner(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            switch (stage)
            {
                case "ar_setup":
                {
                    if (an == null || an.floors < 2 || an.groundZ < 90)
                    {
                        Result("slabarmor setup", false, $"needs 2+ floors and 90+ ground rows (-projectzfloors=3); floors {an?.floors}, ground {an?.groundZ}");
                        Finish();
                        return true;
                    }
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    map.weatherManager.TransitionTo(WeatherDefOf.Clear);
                    ExClear(map, new CellRect(5, 5, 100, 85));
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, true);
                        Teleport(p, new IntVec3(120 + Colonists(map).IndexOf(p), 0, 15));
                    }
                    // defs: every slab follows the rule, damaged slabs keep 35, the armoured ones say so
                    List<string> bad = new List<string>(), armored = new List<string>();
                    foreach (TerrainDef s in SlabDefs.All)
                    {
                        ThingDef st = SlabDefs.StuffOf(s);
                        bool arm = SaArmoredSpec(st);
                        if (arm)
                        {
                            armored.Add($"{st.defName} ({st.stuffProps.statFactors.GetStatFactorFromList(StatDefOf.MaxHitPoints)})");
                        }
                        if (s.destroyOnBombDamageThreshold != (arm ? 55f : 35f) || arm != s.description.Contains("PZ_ArmoredSlabDesc".Translate()))
                        {
                            bad.Add($"{s.defName} {s.destroyOnBombDamageThreshold}");
                        }
                    }
                    bad.AddRange(SlabDefs.AllDamaged.Where(d => d.destroyOnBombDamageThreshold != 35f).Select(d => $"{d.defName} {d.destroyOnBombDamageThreshold}"));
                    Result("defs: armoured metal slabs (Metallic, MaxHitPoints factor 2+) 55 and a line in the description, every other slab and every damaged slab 35",
                        bad.Count == 0 && armored.Any(a => a.StartsWith("Plasteel ")) && armored.Any(a => a.StartsWith("Uranium ")) && !SaArmoredSpec(ThingDefOf.Steel),
                        $"armoured: {string.Join(", ", armored)}; wrong: {(bad.Count == 0 ? "none" : string.Join(", ", bad.Take(8)))}");
                    for (int i = 0; i < 5; i++)
                    {
                        SxPavilion(map, SaG(i), 1, SaSlab(i));
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        SxPavilion(map, SaM(i), 1, SaSlab(new[] { 1, 3, 4 }[i]));
                    }
                    SxPavilion(map, saS, 1, SaSlab(4));
                    if (!GenRadial.RadialCellsAround(SaMid(SaG(3), map), 1.9f, true).All(c => SxState(c, map) == "intact"))
                    {
                        Result("slabarmor setup", false, "pavilions not laid");
                    }
                    saIdx = 0;
                    saThrows = 0;
                    Wait(5, "ar_g");
                    return true;
                }

                // ---- G: frag grenades until the slab breaks (at most 3)
                case "ar_g":
                {
                    if (saIdx >= 5)
                    {
                        stage = "ar_gsum";
                        return true;
                    }
                    IntVec3 mid = SaMid(SaG(saIdx), map);
                    if (saPawn == null)
                    {
                        saPawn = SxPawn(map, mid + IntVec3.North * 2);
                    }
                    sxFrom = sxBlasts.Count;
                    SxThrow(saPawn, mid);
                    SaTickUntil(1500, () => SxBlastDone(map), "ar_g2", 1);
                    return true;
                }
                case "ar_g2":
                {
                    tickWatch = null;
                    IntVec3 mid = SaMid(SaG(saIdx), map);
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault();
                    saThrows++;
                    string st = SxState(mid, map);
                    Info($"{saStuffNames[saIdx]} grenade {saThrows}: blast at {b?.centre} ({b?.dam} {b?.amount}), middle {st}; blast area {SaRing(mid, 1.9f, map)}");
                    if (b == null || b.centre != mid)
                    {
                        Result($"grenades: {saStuffNames[saIdx]} throw {saThrows} lands on the middle", false, $"blast at {b?.centre}, want {mid}");
                    }
                    if (st == "gone" || saThrows >= SaMaxThrows)
                    {
                        saGrenadesToBreak[saIdx] = st == "gone" ? saThrows : -1;
                        if (saGrenadesToBreak[saIdx] < 0)
                        {
                            Result($"grenades: {saStuffNames[saIdx]} slab untouched after {SaMaxThrows} grenades (whole blast area intact)",
                                GenRadial.RadialCellsAround(mid, 1.9f, true).All(c => SxState(c, map) == "intact"), SaRing(mid, 1.9f, map));
                        }
                        saPawn.Destroy();
                        saPawn = null;
                        saIdx++;
                        saThrows = 0;
                    }
                    StartTicks(10, "ar_g");
                    return true;
                }
                case "ar_gsum":
                {
                    string table = string.Join(", ", Enumerable.Range(0, 5).Select(i => $"{saStuffNames[i]} {(saGrenadesToBreak[i] < 0 ? $"survives {SaMaxThrows}" : saGrenadesToBreak[i].ToString())}"));
                    Info("grenades to break: " + table);
                    Result("grenades: wood breaks on 1, steel and granite on 2 (unchanged)", saGrenadesToBreak[0] == 1 && saGrenadesToBreak[1] == 2 && saGrenadesToBreak[2] == 2, table);
                    Result("grenades: plasteel and uranium survive every grenade", saGrenadesToBreak[3] < 0 && saGrenadesToBreak[4] < 0, table);
                    saIdx = 0;
                    stage = "ar_m";
                    return true;
                }

                // ---- M: one mortar HE shell per pavilion
                case "ar_m":
                {
                    if (saIdx >= 3)
                    {
                        stage = "ar_msum";
                        return true;
                    }
                    if (saPawn == null)
                    {
                        saPawn = SxPawn(map, new IntVec3(70, 0, 8));
                    }
                    IntVec3 g = SaM(saIdx).CenterCell;
                    ThingDef shell = DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive").projectileWhenLoaded;
                    Projectile pr = (Projectile)GenSpawn.Spawn(shell, saPawn.Position, map);
                    sxFrom = sxBlasts.Count;
                    pr.Launch(saPawn, saPawn.DrawPos, new LocalTargetInfo(g), new LocalTargetInfo(g), ProjectileHitFlags.IntendedTarget);
                    Info($"mortar: {shell.defName} launched by {saPawn.LabelShort} at the ground {g} under the {new[] { "steel", "plasteel", "uranium" }[saIdx]} pavilion");
                    SaTickUntil(2000, () => SxBlastDone(map), "ar_m2", 1);
                    return true;
                }
                case "ar_m2":
                {
                    tickWatch = null;
                    IntVec3 mid = SaMid(SaM(saIdx), map);
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault();
                    saMortarState[saIdx] = SxState(mid, map);
                    string ring = SaRing(mid, 2.9f, map);
                    Info($"mortar {saIdx}: blast at {b?.centre} ({b?.dam} {b?.amount}), middle {saMortarState[saIdx]}; blast area {ring}");
                    Result($"mortar: the shell explodes on the {new[] { "steel", "plasteel", "uranium" }[saIdx]} slab (50 Bomb)", b != null && b.centre == mid && b.dam == "Bomb" && b.amount == 50, $"blast at {b?.centre} ({b?.dam} {b?.amount})");
                    if (saIdx > 0)
                    {
                        Result($"mortar: the {(saIdx == 1 ? "plasteel" : "uranium")} slab is untouched (whole blast area intact)", GenRadial.RadialCellsAround(mid, 2.9f, true).All(c => SxState(c, map) == "intact"), ring);
                    }
                    saIdx++;
                    StartTicks(10, "ar_m");
                    return true;
                }
                case "ar_msum":
                {
                    Result("mortar: the steel slab is damaged (unchanged)", saMortarState[0] == "damaged", $"steel {saMortarState[0]}, plasteel {saMortarState[1]}, uranium {saMortarState[2]}");
                    saPawn?.Destroy();
                    saPawn = null;
                    // ---- H: heavier blasts on the grenade pavilions
                    sxFrom = sxBlasts.Count;
                    GenExplosion.DoExplosion(SaMid(SaG(3), map), map, 1.9f, DamageDefOf.Bomb, null, damAmount: 60);
                    GenExplosion.DoExplosion(SaMid(SaG(4), map), map, 1.9f, DamageDefOf.Bomb, null, damAmount: 55);
                    SeedTicks("slabarmor_ar_h");
                    StartTicks(60, "ar_h");
                    return true;
                }
                case "ar_h":
                {
                    IntVec3 p = SaMid(SaG(3), map), u = SaMid(SaG(4), map);
                    TerrainDef pt = SlabDamage.SlabAt(p, map), ut = SlabDamage.SlabAt(u, map);
                    Result("heavy: a 60 blast damages the plasteel slab, an exactly 55 blast the uranium one",
                        pt != null && pt == SlabDefs.DamagedOf(SaSlab(3)) && ut != null && ut == SlabDefs.DamagedOf(SaSlab(4)),
                        $"plasteel middle {pt?.defName} (area {SaRing(p, 1.9f, map)}), uranium middle {ut?.defName} (area {SaRing(u, 1.9f, map)})");
                    Result("heavy: their damaged slabs keep 35", pt?.destroyOnBombDamageThreshold == 35f && ut?.destroyOnBombDamageThreshold == 35f, $"{pt?.destroyOnBombDamageThreshold}, {ut?.destroyOnBombDamageThreshold}");
                    saIdx = 3;
                    stage = "ar_h2";
                    return true;
                }
                case "ar_h2":
                {
                    if (saIdx >= 5)
                    {
                        stage = "ar_s";
                        return true;
                    }
                    IntVec3 mid = SaMid(SaG(saIdx), map);
                    saPawn = SxPawn(map, mid + IntVec3.North * 2);
                    sxFrom = sxBlasts.Count;
                    SxThrow(saPawn, mid);
                    SaTickUntil(1500, () => SxBlastDone(map), "ar_h3", 1);
                    return true;
                }
                case "ar_h3":
                {
                    tickWatch = null;
                    IntVec3 mid = SaMid(SaG(saIdx), map);
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault();
                    Result($"heavy: a frag grenade then breaks the damaged {saStuffNames[saIdx]} slab", b != null && b.centre == mid && SxState(mid, map) == "gone",
                        $"blast at {b?.centre} ({b?.amount}), middle {SxState(mid, map)}; area {SaRing(mid, 1.9f, map)}");
                    saPawn.Destroy();
                    saPawn = null;
                    saIdx++;
                    StartTicks(10, "ar_h2");
                    return true;
                }

                // ---- S: power beam over a uranium pavilion
                case "ar_s":
                {
                    saPawn = SxPawn(map, new IntVec3(saS.CenterCell.x + 15, 0, 82));
                    Verb v = SxTargeter(saPawn, "OrbitalTargeterPowerBeam");
                    saBeamStart = -1;
                    saBeamSeq.Clear();
                    v?.OrderForceTarget(new LocalTargetInfo(saS.CenterCell));
                    Info($"power beam: {saPawn.LabelShort} at {saPawn.Position} uses {v} on the ground {saS.CenterCell} under the uranium pavilion: job {saPawn.CurJob?.def?.defName}");
                    SaTickUntil(3000, () => SaBeamWatch(map), "ar_s2", 1);
                    return true;
                }
                case "ar_s2":
                {
                    tickWatch = null;
                    string seq = string.Join(" -> ", saBeamSeq);
                    Result("sky strike: the power beam damages the uranium slab on its first hit, then breaks it", saBeamSeq.Count >= 3 && saBeamSeq[0] == "intact" && saBeamSeq[1] == "damaged" && saBeamSeq[2] == "gone",
                        $"middle {seq}");
                    SxFireOut(map);
                    saPawn.Destroy();
                    saPawn = null;
                    Info($"counters: slabs damaged {SlabDamage.Damaged}, broken {SlabDamage.Broken}, beam hits {SkyStrikes.BeamHits}");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static bool SaBeamWatch(Map map)
        {
            IntVec3 mid = SaMid(saS, map);
            string st = SxState(mid, map);
            if (saBeamSeq.Count == 0 || saBeamSeq.Last() != st)
            {
                saBeamSeq.Add(st);
                if (saBeamSeq.Count > 1)
                {
                    Info($"power beam: middle {st} at t+{Find.TickManager.TicksGame - saBeamStart}");
                }
            }
            Thing beam = map.listerThings.ThingsOfDef(ThingDefOf.PowerBeam).FirstOrDefault();
            if (saBeamStart < 0)
            {
                if (beam != null)
                {
                    saBeamStart = Find.TickManager.TicksGame;
                }
                return false;
            }
            return beam == null && Find.TickManager.TicksGame - saBeamStart > 10;
        }
    }
}
