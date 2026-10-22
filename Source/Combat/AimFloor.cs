using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // Directional effects between floors (flame spews, sprays, burners, beams; user decisions 2026-10-20): the game works
    // out a cone, a line or a sweep from the caster's cell in map rows, where another floor is a whole band of rows away
    // (always due north or south). One mechanism, "aim on the target's floor":
    //   - while a listed vanilla method (Rows) runs for a caster whose target is on another floor, the caster's position
    //     (Position, DrawPos) reads as a virtual one: its ground cell on the target's floor. The game's own geometry
    //     then lays the cone, the spray arc or the beam sweep on the target's floor, toward the target;
    //   - the line-of-sight checks in those methods (TryFindShootLineFromTo, GenSight.LineOfSight and
    //     LastPointOnLineOfSight from the virtual cell) ask Los3D from the REAL caster cell instead, so cells hidden under
    //     a slab or behind a wall or roof are left out (decision 2); a beam stops at a visible wall on the target's floor
    //     or hits nothing (decision 3);
    //   - things the methods put in the world at the caster (a projectile's launch, a spray's source, an effecter's or
    //     overlay's source end) are put back at the real caster (the sinks), so they leave from where it stands; IsoRim
    //     draws what runs between two floors (Floors.FlatDelta).
    // A transpiler swaps those calls in the listed methods only; a prefix opens the scope ("own" rows) and a finalizer
    // closes it; "inner" rows (local functions, iterators, the explosion's cell picker) use the scope of the method that
    // calls them. Same floor and maps without floors: no scope, every call is the game's own (one list-count test).
    public static class AimFloor
    {
        // Test only (-projectznoaimfloor): nothing patched, the game's own directions.
        public static bool Off;

        // tests: scopes opened, Los3D checks made, sinks moved back
        public static int Scopes, Checks, SinksMoved;

        public sealed class Row
        {
            public readonly string type, method;   // method: exact name, or a prefix ending in '*'
            public readonly string target;         // "arg0": the first argument; "current": the verb's target; "inner"
            public MethodBase found;
            public int swaps;

            public Row(string type, string method, string target)
            {
                this.type = type;
                this.method = method;
                this.target = target;
            }

            public string Name => type.Substring(type.LastIndexOf('.') + 1) + "." + method;

            public string Entry => "AimFloor:" + Name;   // its FailSoft entry name
        }

        // The reviewed list (vanilla 1.7 + DLCs): every method that turns the caster's cell into a direction, a cone, a
        // line or a sweep for an effect aimed at a target.
        public static readonly Row[] Rows =
        {
            new Row("RimWorld.CompAbilityEffect_FireSpew", "AffectedCells", "arg0"),             // cone (fire spew, hellcat/incinerator burner)
            new Row("RimWorld.CompAbilityEffect_FireSpew", "<AffectedCells>g__CanUseCell*", "inner"),
            new Row("RimWorld.CompAbilityEffect_SprayLiquid", "AffectedCells", "arg0"),          // acid/foam/sludge spray: neighbour order
            new Row("RimWorld.CompAbilityEffect_SprayLiquid", "Apply", "arg0"),                  // its drops: aimed at their cells
            new Row("RimWorld.CompAbilityEffect_Burner", "<GetPreCastActions>b__*", "arg0"),     // burner pre-cast streams
            new Row("Verse.Verb_ShootBeam", "CalculatePath", "arg0"),                            // beam sweep
            new Row("Verse.Verb_ShootBeam", "DrawHighlight", "arg0"),
            new Row("Verse.Verb_ShootBeam", "TryCastShot", "current"),
            new Row("Verse.Verb_ShootBeam", "TryGetHitCell", "inner"),
            new Row("Verse.Verb_ShootBeam+<GetBeamHitNeighbourCells>d__*", "MoveNext", "inner"),
            new Row("Verse.Verb_ShootBeam", "ApplyDamage", "current"),
            new Row("Verse.Verb_ShootBeam", "BurstingTick", "current"),
            new Row("Verse.Verb_ShootBeam", "get_AimAngleOverride", "current"),
            new Row("Verse.Verb_ArcSprayIncinerator", "TryCastShot", "current"),
            new Row("Verse.Verb_SpewFire", "TryCastShot", "current"),                            // mini-flameblaster
            new Row("Verse.Verb_ArcSpray", "PreparePath", "current"),                            // foam turret arc
            new Row("Verse.Verb_Spray", "TryCastShot", "current"),
            new Row("Verse.Verb_Spray", "get_AimAngleOverride", "current"),
            new Row("Verse.Verb_ArcSprayProjectile", "HitCell", "current"),
            new Row("Verse.DamageWorker", "ExplosionCellsToHit", "inner"),                      // the spew's cone explosion (its sight lines)
        };

        private struct Scope
        {
            public bool on;
            public Thing caster;
            public Map map;
            public IntVec3 real, virt;
            public int k;   // the target's floor
        }

        private static readonly List<Scope> stack = new List<Scope>();

        // ---- the scope

        public static bool Active => stack.Count > 0 && stack[stack.Count - 1].on;

        public static void Enter(object __instance, object[] __args, MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            Row row = RowOf(__originalMethod);
            if (row == null)
            {
                return;
            }
            Scope s = default;
            Thing caster = __instance is Verb v ? v.caster : (__instance as AbilityComp)?.parent?.pawn;
            IntVec3 t = IntVec3.Invalid;
            if (row.target == "current" && __instance is Verb vc)
            {
                t = vc.CurrentTarget.IsValid ? vc.CurrentTarget.Cell : IntVec3.Invalid;
            }
            else if (row.target == "arg0" && __args != null && __args.Length > 0)
            {
                switch (__args[0])
                {
                    case LocalTargetInfo lt: t = lt.IsValid ? lt.Cell : IntVec3.Invalid; break;
                    case IntVec3 c: t = c; break;
                    case Vector3 p: t = p.ToIntVec3(); break;
                }
            }
            Map map = caster != null && caster.Spawned ? caster.Map : null;
            if (map != null && t.IsValid && t.InBounds(map) && ProjectZ.HasAnnex(map) && Heights.CrossFloor(caster.Position, t, map))
            {
                IntVec3 virt = Heights.OnFloorOf(caster.Position, t, map);
                if (virt.IsValid && virt != caster.Position)
                {
                    s = new Scope { on = true, caster = caster, map = map, real = caster.Position, virt = virt, k = ProjectZ.FloorOf(t, map) };
                    Scopes++;
                }
            }
            // a same-floor call inside a cross-floor one (another verb) closes the scope for its run
            if (s.on || stack.Count > 0)
            {
                stack.Add(s);
                __state = true;
            }
        }

        public static void Leave(bool __state)
        {
            if (__state && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        private static bool Top(out Scope s)
        {
            if (stack.Count == 0)
            {
                s = default;
                return false;
            }
            s = stack[stack.Count - 1];
            return s.on;
        }

        // The explosion at c is the scope's cone (Verb_SpewFire's, centred on the virtual cell): only the target's floor.
        public static bool IsVirtualCentre(IntVec3 c, Map map)
        {
            return Top(out Scope s) && s.map == map && c == s.virt;
        }

        // A cell of the target's floor seen from the real caster (Los3D: slabs, roofs and walls block); on an upper floor
        // only a cell with a floor (open air holds nothing to burn, as for explosions).
        private static bool Visible(Scope s, IntVec3 c)
        {
            Checks++;
            return c.InBounds(s.map) && ProjectZ.FloorOf(c, s.map) == s.k && (s.k <= 0 || ProjectZ.HasFloor(c, s.map)) && Los3D.CanSee(s.real, c, s.map);
        }

        // ---- sources: the caster's position on the target's floor

        public static IntVec3 Position(Thing t)
        {
            if (stack.Count > 0 && Top(out Scope s) && t == s.caster)
            {
                return s.virt;
            }
            return t.Position;
        }

        public static Vector3 DrawPos(Thing t)
        {
            Vector3 d = t.DrawPos;
            if (stack.Count > 0 && Top(out Scope s) && t == s.caster)
            {
                d += (s.virt - s.real).ToVector3();
            }
            return d;
        }

        // ---- sight: Los3D from the real caster

        // A sight question asked in a scope: from the virtual cell (any end: a cell off the target's floor is never seen
        // from it, the cone stops at that floor's edge), or from the real cell toward the target's floor.
        private static bool Asks(IntVec3 start, IntVec3 end, Map map, out Scope s)
        {
            if (stack.Count == 0 || !Top(out s) || s.map != map)
            {
                s = default;
                return false;
            }
            return start == s.virt || start == s.real && end.InBounds(map) && ProjectZ.FloorOf(end, map) == s.k;
        }

        public static bool TryFindShootLineFromTo(Verb verb, IntVec3 root, LocalTargetInfo targ, out ShootLine resultingLine, bool ignoreRange)
        {
            if (targ.IsValid && Asks(root, targ.Cell, verb.caster?.Map, out Scope s))
            {
                resultingLine = new ShootLine(s.virt, targ.Cell);
                return Visible(s, targ.Cell);
            }
            return verb.TryFindShootLineFromTo(root, targ, out resultingLine, ignoreRange);
        }

        public static bool LineOfSight(IntVec3 start, IntVec3 end, Map map, bool skipFirstCell, Func<IntVec3, bool> validator, int halfXOffset, int halfZOffset)
        {
            if (Asks(start, end, map, out Scope s))
            {
                return Visible(s, end);
            }
            return GenSight.LineOfSight(start, end, map, skipFirstCell, validator, halfXOffset, halfZOffset);
        }

        public static bool LineOfSight3(IntVec3 start, IntVec3 end, Map map)
        {
            if (Asks(start, end, map, out Scope s))
            {
                return Visible(s, end);
            }
            return GenSight.LineOfSight(start, end, map);
        }

        // Where a beam (or a burner stream) from the virtual cell toward end stops: end when the caster sees it; else the
        // first wall (validator false) on the way that the caster sees (the beam hits it); else nothing (Invalid: hidden
        // under a slab or roof, or the line leaves the target's floor first).
        public static IntVec3 LastPointOnLineOfSight(IntVec3 start, IntVec3 end, Func<IntVec3, bool> validator, bool skipFirstCell)
        {
            if (stack.Count == 0 || !Top(out Scope s) || start != s.virt)
            {
                return GenSight.LastPointOnLineOfSight(start, end, validator, skipFirstCell);
            }
            if (Visible(s, end))
            {
                return end;
            }
            foreach (IntVec3 c in GenSight.PointsOnLineOfSight(start, end))
            {
                if (c == start)
                {
                    continue;
                }
                if (!c.InBounds(s.map) || ProjectZ.FloorOf(c, s.map) != s.k)
                {
                    break;
                }
                if (c != end && !validator(c) && Visible(s, c))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        // ---- sinks: what leaves from the caster leaves from where it really stands

        private static IntVec3 Real(IntVec3 c)
        {
            if (Top(out Scope s) && c == s.virt)
            {
                SinksMoved++;
                return s.real;
            }
            return c;
        }

        public static Thing SpawnDef(ThingDef def, IntVec3 loc, Map map, WipeMode wipeMode)
        {
            return GenSpawn.Spawn(def, stack.Count > 0 ? Real(loc) : loc, map, wipeMode);
        }

        public static Thing SpawnThing(Thing newThing, IntVec3 loc, Map map, WipeMode wipeMode)
        {
            return GenSpawn.Spawn(newThing, stack.Count > 0 ? Real(loc) : loc, map, wipeMode);
        }

        // A spray's drop: from the real caster, aimed at its cell (Flights: it flies the clear line there, not a miss that
        // stops on the slab; the cells were picked as seen).
        public static void Launch(Projectile p, Thing launcher, Vector3 origin, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget, ProjectileHitFlags hitFlags, bool preventFriendlyFire, Thing equipment, ThingDef targetCoverDef)
        {
            bool aimed = false;
            if (stack.Count > 0 && Top(out Scope s) && launcher == s.caster)
            {
                Vector3 shift = (s.virt - s.real).ToVector3();
                if ((origin - (s.caster.DrawPos + shift)).sqrMagnitude < 0.01f)
                {
                    origin -= shift;
                    SinksMoved++;
                }
                aimed = usedTarget.IsValid && ProjectZ.FloorOf(usedTarget.Cell, s.map) == s.k;
            }
            Flights.AimedAtCell = aimed;
            try
            {
                p.Launch(launcher, origin, usedTarget, intendedTarget, hitFlags, preventFriendlyFire, equipment, targetCoverDef);
            }
            finally
            {
                Flights.AimedAtCell = false;
            }
        }

        public static Effecter EffecterSpawn2(EffecterDef def, IntVec3 targetA, IntVec3 targetB, Map map, float scale)
        {
            return def.Spawn(stack.Count > 0 ? Real(targetA) : targetA, targetB, map, scale);
        }

        public static void AddEffecterToMaintain(Verb verb, Effecter eff, IntVec3 posA, IntVec3 posB, int ticks, Map map)
        {
            verb.AddEffecterToMaintain(eff, stack.Count > 0 ? Real(posA) : posA, posB, ticks, map);
        }

        public static MoteDualAttached MakeInteractionOverlay(ThingDef moteDef, TargetInfo A, TargetInfo B)
        {
            if (stack.Count > 0 && !A.HasThing && A.IsValid)
            {
                A = new TargetInfo(Real(A.Cell), A.Map);
            }
            return MoteMaker.MakeInteractionOverlay(moteDef, A, B);
        }

        public static void UpdateTargets(MoteDualAttached mote, TargetInfo a, TargetInfo b, Vector3 offsetA, Vector3 offsetB)
        {
            if (stack.Count > 0 && !a.HasThing && a.IsValid)
            {
                a = new TargetInfo(Real(a.Cell), a.Map);
            }
            mote.UpdateTargets(a, b, offsetA, offsetB);
        }

        // An incinerator/burner stream: its source back at the real caster (its target stays on the target's floor).
        public static void SprayAdd(IncineratorSpray spray, IncineratorProjectileMotion proj)
        {
            if (stack.Count > 0 && Top(out Scope s))
            {
                proj.worldSource -= (s.virt - s.real).ToVector3();
                SinksMoved++;
            }
            spray.Add(proj);
        }

        // ---- patching

        private static readonly Dictionary<MethodBase, Row> rowOf = new Dictionary<MethodBase, Row>();

        private static Row RowOf(MethodBase m) => m != null && rowOf.TryGetValue(m, out Row r) ? r : null;

        public static void Patch(Harmony h)
        {
            Off = GenCommandLine.CommandLineArgPassed("projectznoaimfloor");
            if (Off || !CombatGate.Enabled)
            {
                Log.Message("[ProjectZ] aim on the target's floor: off" + (Off ? " (-projectznoaimfloor)" : " (combat between floors off)"));
                return;
            }
            Assembly game = typeof(Pawn).Assembly;
            foreach (Row r in Rows)
            {
                r.found = Resolve(game, r);
                if (r.found != null)
                {
                    rowOf[r.found] = r;
                }
            }
            // entry by entry (FailSoft "AimFloor:<Type.method>", in the combat group): a row not found (a game update?) or
            // swapping nothing stays the game's, the other rows apply
            HarmonyMethod tr = new HarmonyMethod(typeof(AimFloor), nameof(Transpiler));
            HarmonyMethod pre = new HarmonyMethod(typeof(AimFloor), nameof(Enter)) { priority = Priority.First };
            HarmonyMethod fin = new HarmonyMethod(typeof(AimFloor), nameof(Leave));
            FailSoft.BeginList("aim on the target's floor");
            int patched = 0;
            foreach (Row r in Rows)
            {
                Row row = r;
                bool inner = r.target == "inner";
                if (FailSoft.Entry(r.Entry, r.found, prefix: inner ? null : pre, transpiler: tr, finalizer: inner ? null : fin,
                    check: () => row.swaps > 0 ? null : "no call to swap found", group: FailSoft.Combat))
                {
                    patched++;
                }
            }
            // the aim pie (after IsoRim's prefix, which draws a 3D figure's pie at its muzzle itself)
            bool pie = FailSoft.Entry("AimFloor:GenDraw.DrawAimPie", AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawAimPie)),
                prefix: new HarmonyMethod(typeof(AimFloor), nameof(AimPie)) { priority = Priority.Low }, group: FailSoft.Combat);
            FailSoft.EndList();
            Log.Message($"[ProjectZ] aim on the target's floor: {patched} of {Rows.Length} methods patched, {Rows.Sum(r => r.swaps)} calls swapped, aim pie {(pie ? "on" : "off")}");
        }

        // tests: aim pies turned toward another floor, the last one's facing
        public static int Pies;
        public static float LastPieFacing;

        // The aim pie of a selected pawn warming up (GenDraw.DrawAimPie, on the ground by the shooter) toward a target on
        // another floor: its direction from the shooter's column on the target's floor (the game's would be a whole band
        // of rows north or south), so it points at the target as seen. Same floor: the game's own.
        public static bool AimPie(Thing shooter, LocalTargetInfo target, int degreesWide, float offsetDist, bool __runOriginal)
        {
            if (!__runOriginal || shooter == null || !shooter.Spawned || !target.IsValid)
            {
                return __runOriginal;
            }
            Map map = shooter.Map;
            IntVec3 t = target.Cell;
            if (!t.InBounds(map) || !ProjectZ.HasAnnex(map) || !Heights.CrossFloor(shooter.Position, t, map))
            {
                return true;
            }
            IntVec3 virt = Heights.OnFloorOf(shooter.Position, t, map);
            if (!virt.IsValid || virt == shooter.Position)
            {
                return true;
            }
            float facing = 0f;
            if (t != virt)
            {
                facing = target.Thing == null ? (t - virt).AngleFlat : (target.Thing.DrawPos - virt.ToVector3Shifted()).AngleFlat();
            }
            Pies++;
            LastPieFacing = facing;
            GenDraw.DrawAimPieRaw(shooter.DrawPos + new Vector3(0f, offsetDist, 0f), facing, degreesWide);
            return false;
        }

        private static MethodBase Resolve(Assembly game, Row r)
        {
            List<Type> types = new List<Type>();
            if (r.type.EndsWith("*"))
            {
                int plus = r.type.IndexOf('+');
                Type outer = game.GetType(r.type.Substring(0, plus), false);
                string prefix = r.type.Substring(plus + 1).TrimEnd('*');
                if (outer != null)
                {
                    types.AddRange(outer.GetNestedTypes(AccessTools.all).Where(n => n.Name.StartsWith(prefix)));
                }
            }
            else
            {
                Type t = game.GetType(r.type, false);
                if (t != null)
                {
                    types.Add(t);
                }
            }
            foreach (Type t in types)
            {
                foreach (MethodInfo m in t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    bool match = r.method.EndsWith("*") ? m.Name.StartsWith(r.method.TrimEnd('*')) : m.Name == r.method;
                    if (!match || m.GetMethodBody() == null)
                    {
                        continue;
                    }
                    if (r.type == "Verse.DamageWorker" && m.GetParameters().Length != 6)
                    {
                        continue;   // ExplosionCellsToHit(center, map, radius, los1, los2, angle), not the Explosion overload
                    }
                    if (r.type == "RimWorld.CompAbilityEffect_Burner" && m.GetParameters().Length != 2)
                    {
                        continue;
                    }
                    return m;
                }
            }
            return null;
        }

        private static readonly MethodInfo mPosition = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));
        private static readonly MethodInfo mDrawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
        private static readonly MethodInfo mShootLine = AccessTools.Method(typeof(Verb), nameof(Verb.TryFindShootLineFromTo));
        private static readonly MethodInfo mLos7 = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>), typeof(int), typeof(int) });
        private static readonly MethodInfo mLos3 = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) });
        private static readonly MethodInfo mLastPoint = AccessTools.Method(typeof(GenSight), nameof(GenSight.LastPointOnLineOfSight));
        private static readonly MethodInfo mSpawnDef = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(WipeMode) });
        private static readonly MethodInfo mSpawnThing = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(WipeMode) });
        private static readonly MethodInfo mLaunch = AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) });
        private static readonly MethodInfo mEffSpawn2 = AccessTools.Method(typeof(EffecterDef), nameof(EffecterDef.Spawn), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(float) });
        private static readonly MethodInfo mAddEff = AccessTools.Method(typeof(Verb), nameof(Verb.AddEffecterToMaintain), new[] { typeof(Effecter), typeof(IntVec3), typeof(IntVec3), typeof(int), typeof(Map) });
        private static readonly MethodInfo mOverlay = AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeInteractionOverlay), new[] { typeof(ThingDef), typeof(TargetInfo), typeof(TargetInfo) });
        private static readonly MethodInfo mUpdTargets = AccessTools.Method(typeof(MoteDualAttached), nameof(MoteDualAttached.UpdateTargets));
        private static readonly MethodInfo mSprayAdd = AccessTools.Method(typeof(IncineratorSpray), nameof(IncineratorSpray.Add));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(RowOf(original)?.Entry, original, instructions, code => Edit(code, original));
        }

        // (the row's own count is its entry's check)
        private static string Edit(List<CodeInstruction> code, MethodBase original)
        {
            Row row = RowOf(original);
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                MethodInfo with = null;
                if (ci.operand is MethodInfo m && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt))
                {
                    if (m == mPosition)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(Position));
                    }
                    else if (m.Name == "get_DrawPos" && typeof(Thing).IsAssignableFrom(m.DeclaringType) && m.GetParameters().Length == 0)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(DrawPos));
                    }
                    else if (m == mShootLine)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(TryFindShootLineFromTo));
                    }
                    else if (m == mLos7)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(LineOfSight));
                    }
                    else if (m == mLos3)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(LineOfSight3));
                    }
                    else if (m == mLastPoint)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(LastPointOnLineOfSight));
                    }
                    else if (m == mSpawnDef)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(SpawnDef));
                    }
                    else if (m == mSpawnThing)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(SpawnThing));
                    }
                    else if (m == mLaunch)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(Launch));
                    }
                    else if (m == mEffSpawn2)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(EffecterSpawn2));
                    }
                    else if (m == mAddEff)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(AddEffecterToMaintain));
                    }
                    else if (m == mOverlay)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(MakeInteractionOverlay));
                    }
                    else if (m == mUpdTargets)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(UpdateTargets));
                    }
                    else if (m == mSprayAdd)
                    {
                        with = AccessTools.Method(typeof(AimFloor), nameof(SprayAdd));
                    }
                }
                if (with != null)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
            }
            if (row != null)
            {
                row.swaps = n;
            }
            ProjectZ.DebugLog($"aim on the target's floor: {original.DeclaringType?.Name}.{original.Name}: {n} calls swapped");
            return null;
        }
    }
}
