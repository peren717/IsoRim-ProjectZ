using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // Facility links across floors (user decision 2026-10-16, "REVISED 7"): every facility link (tool cabinets, vitals
    // monitors, gravship parts to the grav engine, ...) measures the true 3D distance: over the ground plus the height
    // between the two floors (Range3D.Storey per floor). On one floor it is the game's own distance, unchanged.
    //
    // The game measures these on the map's raw rows, where floor 2 is a whole band north: a cabinet straight above a
    // bench never linked, and floor 2's south rows linked to the ground's north rows. Inventory (decompile 1.7):
    //   - CompAffectedByFacilities.CanPotentiallyLinkTo_Static: THE link rule (Vector3.Distance of the true centres vs
    //     maxDistance / minDistance). Every link, relink, placement preview (lines and "potentially linked to" lists, both
    //     from the building and from the facility side, CompFacility) goes through it.
    //   - CompAffectedByFacilities.IsBetter and PotentialThingsToLinkTo's order: which of several facilities wins.
    //   - CompGravshipFacility.CanBeActive and CompInspectStringExtra: the part within maxDistance of the engine.
    //   - PlaceWorker_InRangeOfGravEngine (grav field extender): placement and its ghost lines.
    // The adjacency variants (mustBePlacedAdjacent, bed heads, linear facing) work on cells and stay on one floor (the void
    // rows between bands keep them apart). Link lines are drawn between true centres; IsoRim maps each end to its floor.
    public static class Facilities3D
    {
        public static bool Off;   // test switch (-projectznofacilityfix): the game's raw distances

        // The floor of a true centre (an even-sized thing's centre lies on a cell edge: read the cell just south of it,
        // which is inside the thing); -1 for void rows.
        private static int FloorAt(Vector3 v, Map map) => ProjectZ.FloorOf(new IntVec3(Mathf.FloorToInt(v.x), 0, Mathf.FloorToInt(v.z - 0.01f)), map);

        // Distance between two true centres on map: 3D between floors, the game's own otherwise.
        public static float Distance(Vector3 a, Vector3 b, Map map)
        {
            AnnexMapComponent an = Off ? null : ProjectZ.Annex(map);
            if (an != null && (a.z >= an.groundZ || b.z >= an.groundZ))
            {
                int ka = FloorAt(a, map);
                int kb = FloorAt(b, map);
                if (ka != kb && ka >= 0 && kb >= 0)
                {
                    float dx = a.x - b.x;
                    float dz = (a.z - ka * an.stride) - (b.z - kb * an.stride);
                    float dy = (ka - kb) * Range3D.Storey;
                    return Mathf.Sqrt(dx * dx + dz * dz + dy * dy);
                }
            }
            return Vector3.Distance(a, b);
        }

        // Cells a and b within maxDist: 3D between floors, the game's InHorDistOf otherwise.
        public static bool Within(IntVec3 a, IntVec3 b, float maxDist, Map map)
        {
            if (!Off && map != null && Heights.CrossFloor(a, b, map))
            {
                return Range3D.Distance(a, b, map) <= maxDist;
            }
            return a.InHorDistOf(b, maxDist);
        }

        // Replacements for the game's calls (same arguments, then what gives the map).
        public static float DistanceOnMap(Vector3 a, Vector3 b, Map map) => Distance(a, b, map);

        public static float DistanceForComp(Vector3 a, Vector3 b, ThingComp comp) => Distance(a, b, comp?.parent?.MapHeld);

        public static float DistanceForThing(Vector3 a, Vector3 b, Thing t) => Distance(a, b, t?.MapHeld);

        public static bool InHorDistOfForComp(ref IntVec3 a, IntVec3 b, float maxDist, ThingComp comp) => Within(a, b, maxDist, comp?.parent?.MapHeld);

        public static bool InHorDistOfOnMap(ref IntVec3 a, IntVec3 b, float maxDist, Map map) => Within(a, b, maxDist, map);

        public static bool InHorDistOfCurrentMap(ref IntVec3 a, IntVec3 b, float maxDist) => Within(a, b, maxDist, Find.CurrentMap);
    }

    [HarmonyPatch]
    public static class FacilityDistance_Patch
    {
        private enum MapFrom { Arg, Comp, ThingArg, CurrentMap }

        private static readonly MethodInfo dist = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance), new[] { typeof(Vector3), typeof(Vector3) });
        private static readonly MethodInfo inHor = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) });

        private static readonly Dictionary<MethodBase, (MapFrom from, int arg)> targets = new Dictionary<MethodBase, (MapFrom, int)>();

        // calls swapped per method (logged; the test checks it)
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            void Add(MethodBase m, MapFrom from, int arg, string what)
            {
                if (m == null)
                {
                    Log.Warning("[ProjectZ] facility links across floors: " + what + " not found");
                    return;
                }
                targets[m] = (from, arg);
            }
            Type abf = typeof(CompAffectedByFacilities);
            Add(AccessTools.Method(abf, nameof(CompAffectedByFacilities.CanPotentiallyLinkTo_Static),
                new[] { typeof(ThingDef), typeof(IntVec3), typeof(Rot4), typeof(ThingDef), typeof(IntVec3), typeof(Rot4), typeof(Map) }), MapFrom.Arg, 6, "CanPotentiallyLinkTo_Static");
            Add(AccessTools.Method(abf, "IsBetter"), MapFrom.Comp, 0, "IsBetter");
            // PotentialThingsToLinkTo's order: the orderby lambda (x => Vector3.Distance(myTrueCenter, x.TrueCenter())).
            MethodInfo order = abf.GetNestedTypes(AccessTools.all).SelectMany(t => t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                .FirstOrDefault(m => m.Name.Contains("<PotentialThingsToLinkTo>") && !m.IsStatic && m.GetParameters().Length == 1
                    && typeof(Thing).IsAssignableFrom(m.GetParameters()[0].ParameterType) && Calls(m, dist));
            Add(order, MapFrom.ThingArg, 1, "PotentialThingsToLinkTo's order");
            Add(AccessTools.PropertyGetter(typeof(CompGravshipFacility), nameof(CompGravshipFacility.CanBeActive)), MapFrom.Comp, 0, "CompGravshipFacility.CanBeActive");
            Add(AccessTools.Method(typeof(CompGravshipFacility), nameof(CompGravshipFacility.CompInspectStringExtra)), MapFrom.Comp, 0, "CompGravshipFacility.CompInspectStringExtra");
            Add(AccessTools.Method(typeof(PlaceWorker_InRangeOfGravEngine), nameof(PlaceWorker_InRangeOfGravEngine.AllowsPlacing)), MapFrom.Arg, 4, "PlaceWorker_InRangeOfGravEngine.AllowsPlacing");
            Add(AccessTools.Method(typeof(PlaceWorker_InRangeOfGravEngine), nameof(PlaceWorker_InRangeOfGravEngine.DrawGhost)), MapFrom.CurrentMap, 0, "PlaceWorker_InRangeOfGravEngine.DrawGhost");
            return targets.Keys;
        }

        private static bool Calls(MethodInfo m, MethodInfo callee)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && Equals(p.Value, callee));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            if (!targets.TryGetValue(original, out (MapFrom from, int arg) t))
            {
                return code;
            }
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                bool isDist = ci.Calls(dist);
                if (!isDist && !ci.Calls(inHor))
                {
                    continue;
                }
                string with;
                switch (t.from)
                {
                    case MapFrom.Comp:
                        with = isDist ? nameof(Facilities3D.DistanceForComp) : nameof(Facilities3D.InHorDistOfForComp);
                        break;
                    case MapFrom.ThingArg:
                        with = isDist ? nameof(Facilities3D.DistanceForThing) : null;
                        break;
                    case MapFrom.Arg:
                        with = isDist ? nameof(Facilities3D.DistanceOnMap) : nameof(Facilities3D.InHorDistOfOnMap);
                        break;
                    default:
                        with = isDist ? null : nameof(Facilities3D.InHorDistOfCurrentMap);
                        break;
                }
                if (with == null)
                {
                    continue;
                }
                if (t.from != MapFrom.CurrentMap)
                {
                    code.Insert(i, LoadArg(t.arg).WithLabels(ci.ExtractLabels()));
                    i++;
                }
                ci.opcode = OpCodes.Call;
                ci.operand = AccessTools.Method(typeof(Facilities3D), with);
                n++;
            }
            string key = original.DeclaringType?.Name + "." + original.Name;
            PerMethod[key] = n;
            if (n == 0)
            {
                Log.Warning("[ProjectZ] facility links across floors: no distance call in " + key);
            }
            ProjectZ.DebugLog($"facility links across floors: {key}: {n} distance calls in 3D");
            return code;
        }

        private static CodeInstruction LoadArg(int i)
        {
            switch (i)
            {
                case 0: return new CodeInstruction(OpCodes.Ldarg_0);
                case 1: return new CodeInstruction(OpCodes.Ldarg_1);
                case 2: return new CodeInstruction(OpCodes.Ldarg_2);
                case 3: return new CodeInstruction(OpCodes.Ldarg_3);
                default: return new CodeInstruction(OpCodes.Ldarg_S, (byte)i);
            }
        }
    }
}
