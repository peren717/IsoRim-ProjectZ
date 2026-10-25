using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Combat
{
    // Strikes from the sky with floors (user decisions 2026-10-16, isorim-orbital-strike-3d.md). One rule: anything from
    // the sky hits the CURRENT top of each column it falls on (Column.Top, read again for every hit), and its area
    // is measured over the ground (cells picked around the strike's ground cell, in the ground rows, each lifted to its
    // column top). So orbital strikes burn down layer by layer: each hit on a slab is a heavy hit (SlabDamage), a broken
    // slab opens the floor below, which is the top for the next hit.
    //   - Bombardment (orbital bombardment, the permits' strike and salvo): every shell's cell is picked over the ground and
    //     lifted, again when it lands; its blast is a normal explosion on that top cell (Explosions3D, with blocking: it
    //     explodes ON the top floor, so the slab shields the floors below until it is gone), whose own terrain damage is
    //     the heavy hit on the slabs it reaches (50 damage with no falloff: every top-floor cell in its 6-8 radius). Random fires: lifted.
    //   - Power beam: its fires and flame damage land on column tops; every BeamInterval ticks each column under the beam
    //     (ground radius = half the beam's drawn width, 4 cells) takes one heavy hit on its top slab (one with a wall on it
    //     is shielded by the wall, as from a blast). A 600-tick beam makes 4 hits: two stone floors, or four wood ones.
    //   - The strike's own position (beam, ground glow, sound) follows the top under its centre.
    //   - Shields (CompProjectileInterceptor) cover shells and fires by ground distance.
    //   - Lightning strikes the column top: a storm may hit a roof with floors under it (on the top floor), a strike aimed
    //     at a cell under a floor hits the top of it.
    //   - A forced miss (mortars, rockets: Verb_LaunchProjectile) is picked over the ground and lifted (shells from the sky)
    //     or kept on the target's floor, never in a void row nor on another band by raw rows.
    public static class SkyStrikes
    {
        // Ticks between the power beam's heavy hits.
        public const int BeamInterval = 120;

        public static int BeamHits;   // counters (tests)
        public static int ShellsLifted;

        // A cell over the ground around the strike's ground cell within r (in the ground rows), by vanilla's weight on the
        // ground distance, lifted to its column top.
        public static IntVec3 PickTop(Thing strike, float r, System.Func<float, float> weight)
        {
            Map map = strike.Map;
            IntVec3 g0 = ProjectZ.GroundCellOf(strike.Position, map);
            if (!g0.IsValid)
            {
                return strike.Position;
            }
            int gz = ProjectZ.GroundZ(map);
            IntVec3 g = GenRadial.RadialCellsAround(g0, r, true).Where(x => x.x >= 0 && x.z >= 0 && x.x < map.Size.x && x.z < gz)
                .RandomElementByWeight(x => weight(x.DistanceTo(g0)));
            return Column.Top(g, map);
        }

        // The strike stands on the top under its centre (beam, glow, sound; IsoRim reads the height from the row).
        public static void Follow(Thing strike)
        {
            if (strike == null || !strike.Spawned || ProjectZ.Annex(strike.Map) == null)
            {
                return;
            }
            IntVec3 top = Column.Top(strike.Position, strike.Map);
            if (top.IsValid && top != strike.Position)
            {
                strike.Position = top;
            }
        }

        // Nothing wall-like on the cell (a wall shields its slab cell, as from a blast: DamageWorker.ExplosionAffectCell).
        public static bool Exposed(IntVec3 c, Map map)
        {
            List<Thing> list = c.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].def.Fillage == FillCategory.Full && list[i].def.category != ThingCategory.Mote && list[i].def.category != ThingCategory.Ethereal)
                {
                    return false;
                }
            }
            return true;
        }

        // One heavy hit on the top slab of every column within r of the beam's ground cell.
        public static void BeamHit(Thing beam, float r)
        {
            Map map = beam.Map;
            IntVec3 g0 = ProjectZ.GroundCellOf(beam.Position, map);
            if (!g0.IsValid)
            {
                return;
            }
            int gz = ProjectZ.GroundZ(map);
            foreach (IntVec3 g in GenRadial.RadialCellsAround(g0, r, true).ToList())
            {
                if (g.x < 0 || g.z < 0 || g.x >= map.Size.x || g.z >= gz)
                {
                    continue;
                }
                IntVec3 top = Column.Top(g, map);
                if (ProjectZ.FloorOf(top, map) >= 1 && Exposed(top, map))
                {
                    SlabDamage.HeavyHit(top, map);
                    BeamHits++;
                }
            }
        }

        // Ground distance for the shields (ProjectileInterceptor_GroundDistance_Patch).
        public static bool GroundInHorDistOf(ref IntVec3 a, IntVec3 b, float maxDist, CompProjectileInterceptor comp)
        {
            Map map = comp?.parent?.Map;
            if (map != null && ProjectZ.Annex(map) != null)
            {
                return Heights.GroundDistance(a, b, map) <= maxDist;
            }
            return a.InHorDistOf(b, maxDist);
        }

        // Where lightning strikes: the column top (a random strike: any ground cell whose top is open to the sky).
        public static IntVec3 LightningCell(IntVec3 loc, Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return loc;
            }
            if (!loc.IsValid)
            {
                return CellFinderLoose.TryGetRandomCellWith(sq =>
                {
                    IntVec3 t = Column.Top(sq, map);
                    return t.IsValid && t.Standable(map) && !map.roofGrid.Roofed(t);
                }, map, 1000, out IntVec3 g) ? Column.Top(g, map) : IntVec3.Invalid;
            }
            if (!loc.InBounds(map) || ProjectZ.FloorOf(loc, map) < 0)
            {
                return loc;
            }
            if (map.roofGrid.RoofAt(loc) == PZDefOf.PZ_FloorRoof || ProjectZ.FloorOf(loc, map) >= 1 && !ProjectZ.HasFloor(loc, map))
            {
                IntVec3 top = Column.Top(loc, map);
                return top.IsValid ? top : loc;
            }
            return loc;
        }
    }

    // ---- the strike's position

    [HarmonyPatch(typeof(OrbitalStrike), nameof(OrbitalStrike.StartStrike))]
    public static class OrbitalStrike_StartStrike_Patch
    {
        public static void Prefix(OrbitalStrike __instance) => SkyStrikes.Follow(__instance);
    }

    // ---- bombardment

    [HarmonyPatch(typeof(Bombardment), "GetNextExplosionCell")]
    public static class Bombardment_NextCell_Patch
    {
        private static readonly AccessTools.FieldRef<Bombardment, IntVec3> next = AccessTools.FieldRefAccess<Bombardment, IntVec3>("nextExplosionCell");

        public static bool Prefix(Bombardment __instance)
        {
            if (!__instance.Spawned || ProjectZ.Annex(__instance.Map) == null)
            {
                return true;
            }
            SkyStrikes.Follow(__instance);
            float r = __instance.impactAreaRadius;
            next(__instance) = SkyStrikes.PickTop(__instance, r, d => Bombardment.DistanceChanceFactor.Evaluate(d / r));
            return false;
        }
    }

    // The shell lands on the top of its column as it is when it lands.
    [HarmonyPatch(typeof(Bombardment), "TryDoExplosion")]
    public static class Bombardment_TryDoExplosion_Patch
    {
        public static void Prefix(Bombardment __instance, Bombardment.BombardmentProjectile proj)
        {
            if (proj == null || !__instance.Spawned || ProjectZ.Annex(__instance.Map) == null)
            {
                return;
            }
            IntVec3 top = Column.Top(proj.targetCell, __instance.Map);
            if (top.IsValid && top != proj.targetCell)
            {
                proj.targetCell = top;
                SkyStrikes.ShellsLifted++;
            }
        }
    }

    [HarmonyPatch(typeof(Bombardment), "StartRandomFire")]
    public static class Bombardment_StartRandomFire_Patch
    {
        public static bool Prefix(Bombardment __instance)
        {
            Map map = __instance.Map;
            if (!__instance.Spawned || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            IntVec3 c = SkyStrikes.PickTop(__instance, __instance.randomFireRadius, d => Bombardment.DistanceChanceFactor.Evaluate(d));
            List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor);
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].TryGetComp<CompProjectileInterceptor>().BombardmentCanStartFireAt(__instance, c))
                {
                    return false;
                }
            }
            RoofDef roof = c.GetRoof(map);
            if (roof == null || !roof.isThickRoof)
            {
                FireUtility.TryStartFireIn(c, map, Rand.Range(0.1f, 0.925f), __instance.instigator);
            }
            return false;
        }
    }

    // ---- power beam

    [HarmonyPatch(typeof(PowerBeam), "StartRandomFireAndDoFlameDamage")]
    public static class PowerBeam_Fire_Patch
    {
        private static readonly List<Thing> tmp = new List<Thing>();

        public static bool Prefix(PowerBeam __instance)
        {
            Map map = __instance.Map;
            if (!__instance.Spawned || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            IntVec3 c = SkyStrikes.PickTop(__instance, PowerBeam.Radius, d => 1f - Mathf.Min(d / PowerBeam.Radius, 1f) + 0.05f);
            FireUtility.TryStartFireIn(c, map, Rand.Range(0.1f, 0.925f), __instance.instigator);
            tmp.Clear();
            tmp.AddRange(c.GetThingList(map));
            for (int i = 0; i < tmp.Count; i++)
            {
                // (vanilla's FlameDamageAmountRange 65-100, corpses 5-10)
                int amount = tmp[i] is Corpse ? Rand.RangeInclusive(5, 10) : Rand.RangeInclusive(65, 100);
                Pawn pawn = tmp[i] as Pawn;
                BattleLogEntry_DamageTaken log = null;
                if (pawn != null)
                {
                    log = new BattleLogEntry_DamageTaken(pawn, RulePackDefOf.DamageEvent_PowerBeam, __instance.instigator as Pawn);
                    Find.BattleLog.Add(log);
                }
                tmp[i].TakeDamage(new DamageInfo(DamageDefOf.Flame, amount, 0f, -1f, __instance.instigator, null, __instance.weaponDef)).AssociateWithLog(log);
            }
            tmp.Clear();
            return false;
        }
    }

    // The beam's heavy hits, every BeamInterval ticks of its life.
    [HarmonyPatch(typeof(PowerBeam), "Tick")]
    public static class PowerBeam_Tick_Patch
    {
        private static readonly AccessTools.FieldRef<OrbitalStrike, int> startTick = AccessTools.FieldRefAccess<OrbitalStrike, int>("startTick");

        public static void Postfix(PowerBeam __instance)
        {
            if (__instance.Destroyed || !__instance.Spawned || ProjectZ.Annex(__instance.Map) == null)
            {
                return;
            }
            int passed = Find.TickManager.TicksGame - startTick(__instance);
            if (passed <= 0 || passed % SkyStrikes.BeamInterval != 0)
            {
                return;
            }
            float width = __instance.GetComp<CompOrbitalBeam>()?.Props?.width ?? 8f;
            SkyStrikes.BeamHit(__instance, Mathf.Max(0.9f, width / 2f));
            SkyStrikes.Follow(__instance);
        }
    }

    // ---- shields: ground distance

    [HarmonyPatch]
    public static class ProjectileInterceptor_GroundDistance_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckBombardmentIntercept));
            yield return AccessTools.Method(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.BombardmentCanStartFireAt));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(ProjectileInterceptor_GroundDistance_Patch), original, instructions, code =>
            {
                MethodInfo inHor = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) });
                MethodInfo with = AccessTools.Method(typeof(SkyStrikes), nameof(SkyStrikes.GroundInHorDistOf));
                int n = 0;
                for (int i = 0; i < code.Count; i++)
                {
                    if (code[i].Calls(inHor))
                    {
                        code.Insert(i, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[i]));
                        i++;
                        code[i].opcode = OpCodes.Call;
                        code[i].operand = with;
                        n++;
                    }
                }
                return n == 1 ? null : $"{n} distance calls found (expected 1)";
            });
        }
    }

    // ---- lightning

    [HarmonyPatch(typeof(WeatherEvent_LightningStrike), nameof(WeatherEvent_LightningStrike.DoStrike))]
    public static class Lightning_DoStrike_Patch
    {
        public static void Prefix(ref IntVec3 strikeLoc, Map map) => strikeLoc = SkyStrikes.LightningCell(strikeLoc, map);
    }

    // (the event's own cell, which the bolt is drawn at, too)
    [HarmonyPatch(typeof(WeatherEvent_LightningStrike), nameof(WeatherEvent_LightningStrike.FireEvent))]
    public static class Lightning_FireEvent_Patch
    {
        public static void Prefix(ref IntVec3 ___strikeLoc, Map ___map) => ___strikeLoc = SkyStrikes.LightningCell(___strikeLoc, ___map);
    }

    [HarmonyPatch(typeof(WeatherEvent_LightningStrikeDelayed), nameof(WeatherEvent_LightningStrikeDelayed.WeatherEventTick))]
    public static class LightningDelayed_Tick_Patch
    {
        public static void Prefix(ref IntVec3 ___strikeLoc, Map ___map, int ___age, int ___delay)
        {
            if (___age + 1 == ___delay)
            {
                ___strikeLoc = SkyStrikes.LightningCell(___strikeLoc, ___map);
            }
        }
    }

    // A flashstorm strikes only its centre's band (void barrier: never another band's raw rows nor a void row; a storm
    // cast on an upper floor stays there), and may strike a roof with floors under it (on the top floor).
    [HarmonyPatch(typeof(GameCondition_Flashstorm), "IsGoodLocationForStrike")]
    public static class Flashstorm_GoodLocation_Patch
    {
        public static void Postfix(GameCondition_Flashstorm __instance, IntVec3 loc, ref bool __result)
        {
            Map map = __instance.AffectedMaps?.FirstOrDefault();
            if (map == null || ProjectZ.Annex(map) == null || !loc.InBounds(map))
            {
                return;
            }
            IntVec3 centre = __instance.centerLocation.IsInvalid ? IntVec3.Invalid : __instance.centerLocation.ToIntVec3;
            if (VoidRule.Off ? ProjectZ.IsAnnex(loc, map) : !centre.IsValid || !ProjectZ.SameBand(loc, centre, map))
            {
                __result = false;
                return;
            }
            if (!__result && map.roofGrid.RoofAt(loc) == PZDefOf.PZ_FloorRoof)
            {
                IntVec3 top = Column.Top(loc, map);
                __result = top.IsValid && top.Standable(map) && !map.roofGrid.Roofed(top);
            }
        }
    }

    // ---- forced misses

    // (GetForcedMissTarget is small enough to be inlined into TryCastShot, so its call there is swapped for Pick.)
    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    public static class ForcedMiss_Patch
    {
        public static int Lifted;   // counters (tests)

        private static readonly MethodInfo original = AccessTools.Method(typeof(Verb_LaunchProjectile), "GetForcedMissTarget");
        private static readonly System.Func<Verb_LaunchProjectile, float, IntVec3> vanilla = AccessTools.MethodDelegate<System.Func<Verb_LaunchProjectile, float, IntVec3>>(original);

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
        {
            return FailSoft.Transpile(nameof(ForcedMiss_Patch), method, instructions, code =>
            {
                int n = 0;
                foreach (CodeInstruction ci in code)
                {
                    if (ci.Calls(original))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = AccessTools.Method(typeof(ForcedMiss_Patch), nameof(Pick));
                        n++;
                    }
                }
                return n == 1 ? null : $"{n} forced-miss picks found (expected 1)";
            });
        }

        // The shot's projectile, read before the cast (a mortar's loaded shell is used up before the miss is picked).
        [System.ThreadStatic]
        private static ThingDef casting;

        public static void Prefix(Verb_LaunchProjectile __instance)
        {
            casting = ProjectZ.Annex(__instance.caster?.Map) != null ? __instance.Projectile : null;
        }

        public static IntVec3 Pick(Verb_LaunchProjectile v, float radius)
        {
            IntVec3 r = vanilla(v, radius);
            Remap(v, ref r);
            return r;
        }

        public static void Remap(Verb_LaunchProjectile __instance, ref IntVec3 __result)
        {
            Map map = __instance.caster?.Map;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null)
            {
                return;
            }
            IntVec3 t = __instance.CurrentTarget.Cell;
            IntVec3 gt = ProjectZ.GroundCellOf(t, map);
            if (!gt.IsValid)
            {
                return;
            }
            IntVec3 g = gt + (__result - t);
            g.x = Mathf.Clamp(g.x, 0, map.Size.x - 1);
            g.z = Mathf.Clamp(g.z, 0, an.groundZ - 1);
            bool overhead = (casting ?? __instance.Projectile)?.projectile?.flyOverhead ?? false;
            if (overhead && Lobbed.Any && Lobbed.Declared(__instance, __instance.caster.Position, t) == false)
            {
                overhead = false;   // declared direct (Lobbed): the miss stays on the target's floor, as a bullet's
            }
            int k = ProjectZ.FloorOf(t, map);
            IntVec3 res = overhead ? Column.Top(g, map) : ProjectZ.CellOnFloor(g, Mathf.Max(0, k), map);
            if (res.IsValid && res != __result)
            {
                __result = res;
                Lifted++;
            }
        }
    }
}
