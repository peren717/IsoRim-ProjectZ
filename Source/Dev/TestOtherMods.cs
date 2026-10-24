using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Test mode othermods (-projectzfloors=3, Odyssey): ProjectZ next to other mods' patches on the same game code. Generic:
    // the other mod's defs come from the command line (none named here); each check runs only when its argument is given.
    //   The shipset fixture (a ground ship, decks over it on floors 2 and 3, the limit 500 cutting the top floor first). Then:
    //   O1 (-projectzsubscaffold=<TerrainDef>) another mod's argument-adjusting prefix on GravshipUtility.GetConnectedSubstructure
    //      runs before ProjectZ's replacing prefix: the strip's ten ground cells become that foundation, which the other mod
    //      leaves out of the limit, and the valid set grows by ten (500 -> 510); in either mod load order;
    //   O2 (-projectzengine=<ThingDef>) the footprint preview on a map with floors draws every grav engine: an engine of that
    //      def placed east of the ship and selected adds its rings (cells drawn grow; the shot shows it);
    //   O3 a turret on a floor-2 deck (-projectzturret=<ThingDef>, default the mini turret; -projectzturretlink=<ThingDef>: a
    //      building placed beside it, e.g. a targeting core another mod links) with a raider on the ground in its range.
    //      -projectzselfturn=<degrees per tick>: the test plays "another mod that turns its turret itself" (skips
    //      TurretTop.TurretTopTick and turns the gun toward Api.AimPoint at that speed). Per tick the gun's turn is measured:
    //      a self-turning turret (the test's or another mod's) turns at most its own speed (ProjectZ does not snap it); a
    //      vanilla turret still points at the true direction (ProjectZ's aim); the turret fires and hits the raider.
    public static partial class TestRunner
    {
        private static string omSubscaffold, omEngine, omTurretDef, omLink;
        private static float omSelfTurn;
        private static Building_TurretGun omTurret;
        private static Pawn omRaider;
        private static int omGroundCellsBefore, omC2Before, omTicks, omRaiderHediffs, omHits, omOtherHits;
        private static float omMaxTurn, omLastRot = float.NaN;
        private static readonly List<float> omTurns = new List<float>();
        private static Thing omEngine2;
        private static readonly AccessTools.FieldRef<Building_TurretGun, TurretTop> omTop = AccessTools.FieldRefAccess<Building_TurretGun, TurretTop>("top");

        internal static void OtherModsInit(Harmony h)
        {
            GenCommandLine.TryGetCommandLineArg("projectzsubscaffold", out omSubscaffold);
            GenCommandLine.TryGetCommandLineArg("projectzengine", out omEngine);
            GenCommandLine.TryGetCommandLineArg("projectzturretlink", out omLink);
            omTurretDef = GenCommandLine.TryGetCommandLineArg("projectzturret", out string td) ? td : "Turret_MiniTurret";
            if (GenCommandLine.TryGetCommandLineArg("projectzselfturn", out string st))
            {
                float.TryParse(st, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out omSelfTurn);
            }
            h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OmHitPrefix)));
            if (omSelfTurn > 0f)
            {
                h.Patch(AccessTools.Method(typeof(TurretTop), nameof(TurretTop.TurretTopTick)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OmSelfTurnPrefix)));
            }
        }

        // Hits on the raider by the turret (its projectiles name it as the instigator, whatever verb or projectile class).
        internal static void OmHitPrefix(Thing __instance, DamageInfo dinfo)
        {
            if (omRaider == null || __instance != omRaider || omTurret == null)
            {
                return;
            }
            // the turret, or (another mod's turret) the building or pawn it fires for, with the turret's projectile
            if (dinfo.Instigator == omTurret || (dinfo.Weapon != null && (dinfo.Weapon == omTurret.def || dinfo.Weapon == omTurret.gun?.def)))
            {
                omHits++;
            }
            else if (omOtherHits++ < 5)
            {
                Info($"O3 the raider hit by {dinfo.Instigator?.ToString() ?? "nobody"} with {dinfo.Weapon?.defName ?? "-"} ({dinfo.Def?.defName})");
            }
        }

        // (-projectzselfturn) The test's own "other mod": its turret's game turning is skipped and the gun turns toward
        // Api.AimPoint at most omSelfTurn degrees a tick.
        internal static bool OmSelfTurnPrefix(TurretTop __instance, Building_Turret ___parentTurret)
        {
            if (omTurret == null || ___parentTurret != omTurret)
            {
                return true;
            }
            LocalTargetInfo t = omTurret.CurrentTarget;
            if (t.IsValid)
            {
                float want = (Api.AimPoint(omTurret, t) - omTurret.DrawPos).AngleFlat();
                float cur = __instance.CurRotation;
                __instance.CurRotation = cur + Mathf.Clamp(Mathf.DeltaAngle(cur, want), -omSelfTurn, omSelfTurn);
            }
            return false;
        }

        private static void StepOtherMods(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            switch (stage)
            {
                case "om_setup":
                    SetupShipSet(map, "om_o1");
                    return;

                // ---- O1 another mod's argument-adjusting prefix on the connected substructure
                case "om_o1":
                {
                    if (omSubscaffold == null)
                    {
                        stage = "om_o2";
                        return;
                    }
                    TerrainDef sc = DefDatabase<TerrainDef>.GetNamedSilentFail(omSubscaffold);
                    if (sc == null)
                    {
                        Result("O1 the other foundation exists", false, omSubscaffold);
                        stage = "om_o2";
                        return;
                    }
                    e.ForceSubstructureDirty();
                    omGroundCellsBefore = e.ValidSubstructure.Count;
                    List<Thing> makers = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint);
                    List<IntVec3> inFoot = ssStrip.Where(c => GravshipUtility.InsideFootprint(c, map, makers)).ToList();
                    foreach (IntVec3 c in ssStrip)
                    {
                        map.terrainGrid.SetTerrain(c, sc);
                    }
                    e.ForceSubstructureDirty();
                    HashSet<IntVec3> valid = e.ValidSubstructure;
                    int limit = (int)e.GetStatValue(StatDefOf.SubstructureSupport);
                    Result($"O1 {omSubscaffold} left out of the limit by the other mod: the valid set grows by the strip cells inside the footprint ({inFoot.Count} of {ssStrip.Count})",
                        inFoot.Count > 0 && valid.Count == omGroundCellsBefore + inFoot.Count && inFoot.All(valid.Contains),
                        $"limit {limit}, valid before {omGroundCellsBefore}, after {valid.Count}: {SsFloors(valid, map)}; strip in {ssStrip.Count(valid.Contains)}/{ssStrip.Count} (inside the footprint {inFoot.Count}); all connected {e.AllConnectedSubstructure.Count}; mod order: {ModOrder()}");
                    stage = "om_o2";
                    return;
                }

                // ---- O2 every grav engine's footprint preview
                case "om_o2":
                    if (omEngine == null)
                    {
                        stage = "om_o3";
                        return;
                    }
                    ShotAt(map, 0, SS(24, 10), 40f);
                    Find.Selector.Select(e);
                    Wait(20, "om_o2_one");
                    return;
                case "om_o2_one":
                {
                    omC2Before = ShipRange.LastCells.Values.Sum();
                    ThingDef ed = DefDatabase<ThingDef>.GetNamedSilentFail(omEngine);
                    if (ed == null)
                    {
                        Result("O2 the other engine def exists", false, omEngine);
                        stage = "om_o3";
                        return;
                    }
                    omEngine2 = Place(ed, ed.MadeFromStuff ? GenStuff.DefaultStuffFor(ed) : null, SS(38, 18), map);
                    ShotAt(map, 0, SS(24, 10), 40f);
                    Find.Selector.Select(omEngine2);
                    Wait(20, "om_o2_check");
                    return;
                }
                case "om_o2_check":
                {
                    int after = ShipRange.LastCells.Values.Sum();
                    bool inGroup = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint).Contains(omEngine2);
                    Result($"O2 the footprint preview on a map with floors draws the {omEngine} engine too", omEngine2 is Building_GravEngine && inGroup && ShipRange.LastFrame >= Time.frameCount - 10 && after > omC2Before,
                        $"cells drawn: vanilla engine selected {omC2Before}, with the {omEngine} engine {after}; footprint maker {inGroup}; frame {ShipRange.LastFrame}/{Time.frameCount}");
                    Capture("om_o2_footprint");
                    Wait(5, "om_o3");
                    return;
                }

                // ---- O3 a turret on a deck, a raider on the ground
                case "om_o3":
                {
                    Find.Selector.ClearSelection();
                    ThingDef td = DefDatabase<ThingDef>.GetNamedSilentFail(omTurretDef);
                    if (td == null)
                    {
                        Result("O3 the turret def exists", false, omTurretDef);
                        Finish();
                        return;
                    }
                    omTurret = (Building_TurretGun)Place(td, td.MadeFromStuff ? ThingDefOf.Steel : null, SSF(12, 6, 1, map), map);
                    if (omTurret.TryGetComp<CompPowerTrader>() != null)
                    {
                        Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, SSF(12, 8, 1, map), map);
                        Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, SSF(12, 9, 1, map), map);
                        Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, SSF(12, 10, 1, map), map).TryGetComp<CompRefuelable>()?.Refuel(500f);
                    }
                    if (omLink != null && DefDatabase<ThingDef>.GetNamedSilentFail(omLink) is ThingDef ld)
                    {
                        Thing link = Place(ld, ld.MadeFromStuff ? GenStuff.DefaultStuffFor(ld) : null, SSF(15, 6, 1, map), map);
                        if (link.TryGetComp<CompPowerTrader>() != null)
                        {
                            Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, SSF(15, 7, 1, map), map);
                            Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, SSF(15, 8, 1, map), map);
                            Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, SSF(15, 9, 1, map), map).TryGetComp<CompRefuelable>()?.Refuel(500f);
                        }
                    }
                    Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();
                    omRaider = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
                    omRaider.equipment.DestroyAllEquipment();
                    IntVec3 rc = SS(34, 2);
                    GenSpawn.Spawn(omRaider, rc, map);
                    LordMaker.MakeNewLord(f, new LordJob_DefendPoint(rc, 0.9f, 0.9f, false, false), map, new[] { omRaider });
                    omRaiderHediffs = omRaider.health.hediffSet.hediffs.Count;
                    omTurns.Clear();
                    omMaxTurn = 0f;
                    omLastRot = float.NaN;
                    omTicks = 0;
                    omHits = 0;
                    Info($"O3 turret {omTurret} ({omTurret.GetType().Name}) at {omTurret.Position} (floor {ProjectZ.FloorOf(omTurret.Position, map) + 1}), raider at {rc}; 3D distance {Combat.Range3D.Distance(omTurret.Position, rc, map):0.0}, range {omTurret.AttackVerb?.EffectiveRange:0.0}; self-turn {omSelfTurn}");
                    SeedTicks("om_o3");
                    TickUntil(1500, OmWatch, "om_o3_check", 1);
                    return;
                }
                case "om_o3_check":
                {
                    LocalTargetInfo t = new LocalTargetInfo(omRaider);
                    float trueAngle = (Api.AimPoint(omTurret, t) - omTurret.DrawPos).AngleFlat();
                    float flatAngle = (omRaider.Position.ToVector3Shifted() - omTurret.DrawPos).AngleFlat();
                    float rot = omTop(omTurret).CurRotation;
                    float err = Mathf.Abs(Mathf.DeltaAngle(rot, trueAngle));
                    int shots = global::ProjectZ.Combat.PZCombatStats.shots.Count(s => s.launcher == omTurret.ToString());
                    bool hit = omRaider.Dead || omRaider.Downed || omRaider.health.hediffSet.hediffs.Count > omRaiderHediffs;
                    List<float> moving = omTurns.Where(x => x > 0.001f).ToList();
                    string turnInfo = $"max turn {omMaxTurn:0.000} deg/tick, median while turning {(moving.Count > 0 ? moving.OrderBy(x => x).ElementAt(moving.Count / 2) : 0f):0.000}, ticks turning {moving.Count}/{omTicks}";
                    string aimInfo = $"gun at {rot:0.0}, true direction (Api.AimPoint) {trueAngle:0.0}, raw-cell direction {flatAngle:0.0}, error {err:0.0}";
                    if (omSelfTurn > 0f)
                    {
                        Result($"O3 the self-turning turret turns at its own speed ({omSelfTurn} deg/tick; ProjectZ does not snap it)", omMaxTurn <= omSelfTurn + 0.01f && moving.Count > 1, turnInfo);
                        Result("O3 turning toward Api.AimPoint brings the gun onto the target on the ground", err < 2f, aimInfo);
                    }
                    else if (omTurret.def.thingClass == typeof(Building_TurretGun))
                    {
                        Result("O3 a vanilla turret on a deck points at the raider on the ground (ProjectZ's aim)", err < 2f, aimInfo + "; " + turnInfo);
                    }
                    else
                    {
                        // another mod's own turret class: measured and logged (its own turning; it gets Api.AimPoint from its pack)
                        Result($"O3 {omTurretDef} turns at most a few degrees a tick (not snapped by ProjectZ)", omMaxTurn < 10f && moving.Count > 1, turnInfo + "; " + aimInfo);
                    }
                    Result("O3 the turret fires at the raider on the ground and hits", omHits > 0 && hit, $"hits by the turret {omHits}, shots counted {shots}, raider {(omRaider.Dead ? "dead" : omRaider.Downed ? "downed" : "standing")}, hediffs {omRaiderHediffs} -> {omRaider.health.hediffSet.hediffs.Count}");
                    ShotAt(map, 1, SS(22, 5), 26f);
                    Wait(10, "om_o3_shot");
                    return;
                }
                case "om_o3_shot":
                    Capture("om_o3_turret");
                    Wait(5, "om_end");
                    return;
                case "om_end":
                    Finish();
                    return;
            }
        }

        // Per tick: how far the gun turned (degrees), until the raider is down.
        private static bool OmWatch()
        {
            omTicks++;
            float rot = omTop(omTurret).CurRotation;
            if (!float.IsNaN(omLastRot))
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(omLastRot, rot));
                omTurns.Add(d);
                omMaxTurn = Mathf.Max(omMaxTurn, d);
            }
            omLastRot = rot;
            return omRaider.Dead || omRaider.Downed;
        }

        // The prefixes on GetConnectedSubstructure in the order Harmony runs them (owner, priority, index), and ProjectZ's place
        // in the mod list.
        private static string ModOrder()
        {
            HarmonyLib.Patches pi = Harmony.GetPatchInfo(AccessTools.Method(typeof(GravshipUtility), nameof(GravshipUtility.GetConnectedSubstructure)));
            string pre = pi == null ? "-" : string.Join(", ", pi.Prefixes.OrderByDescending(p => p.priority).ThenBy(p => p.index).Select(p => p.owner + " p" + p.priority + " #" + p.index));
            List<string> ids = LoadedModManager.RunningModsListForReading.Select(m => m.PackageId).ToList();
            return "prefixes run as: " + pre + "; ProjectZ is mod " + ids.FindIndex(x => x.StartsWith("peren.isorimprojectz")) + " of " + ids.Count;
        }
    }
}
