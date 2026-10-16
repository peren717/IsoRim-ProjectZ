using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // The weapon range preview between floors (design "Range preview decided", user 2026-10-16). Where the game draws a
    // shooter's or turret's range ring, ProjectZ adds, in the 3D view:
    //   - a clear ring on the ground and on the viewed floor: the cross-section of the 3D range there (Range3D), the
    //     game's own ring look; the shooter's own floor keeps the game's ring;
    //   - a very faint see-through shell round the muzzle: per direction the range at that height (it bulges downward by
    //     the +1 per floor bonus), cut at the ground, brighter toward its outline (Fresnel), drawn after the light
    //     overlay and depth-tested, so walls in front hide it and it never darkens at night.
    // Without the 3D view (or without IsoRim) nothing is added: the game's ring only.
    public static class RangePreview
    {
        // Colour of the shell (its alpha comes from the Fresnel term) and of the added rings.
        public static readonly Color ShellColor = new Color(0.78f, 0.9f, 1f, 1f);
        public const float ShellAlphaFace = 0.035f;   // looking straight at the shell
        public const float ShellAlphaEdge = 0.3f;     // at its outline
        public const float MaxRange = 75f;            // past this (mortars) the game draws no ring either

        private static int drawnFrame = -1;
        private static readonly HashSet<long> drawnThisFrame = new HashSet<long>();

        // From the game's ring call sites (patches below). caster: the shooter or turret; at: the cell the ring is round.
        public static void Draw(Thing caster, IntVec3 at, float range)
        {
            Map map = Find.CurrentMap;
            if (!CombatGate.Enabled || caster == null || map == null || caster.MapHeld != map || !at.IsValid || range <= 1.5f || range > MaxRange
                || !IsoRimBridge.Present || !IsoRimBridge.View3DOn || ProjectZ.Annex(map) == null)
            {
                return;
            }
            int ks = ProjectZ.FloorOf(at, map);
            IntVec3 g = ProjectZ.GroundCellOf(at, map);
            if (ks < 0 || !g.IsValid)
            {
                return;
            }
            // once a frame per shooter and range (the gizmo and the targeter can both ask)
            if (drawnFrame != Time.frameCount)
            {
                drawnFrame = Time.frameCount;
                drawnThisFrame.Clear();
            }
            if (!drawnThisFrame.Add(((long)caster.thingIDNumber << 20) ^ Mathf.RoundToInt(range * 100f)))
            {
                return;
            }
            try
            {
                int view = FloorView.Current;
                Ring(g, ks, 0, range, map);
                if (view != 0)
                {
                    Ring(g, ks, view, range, map);
                }
                float muzzle = IsoRimBridge.MuzzleHeight(caster);
                RangeShell.Draw(new Vector3(g.x + 0.5f, ks * Range3D.Storey + muzzle, g.z + 0.5f), range, muzzle);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the range preview failed: " + e, 61190321);
            }
        }

        private static void Ring(IntVec3 g, int ks, int k, float range, Map map)
        {
            if (k == ks)
            {
                return;   // the game's own ring
            }
            float r = Range3D.HorizontalReach(range, ks, k);
            if (r < 1f)
            {
                return;
            }
            IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
            if (c.IsValid)
            {
                GenDraw.DrawRadiusRing(c, Mathf.Min(r, GenRadial.MaxRadialPatternRadius - 0.5f), Color.white);
            }
        }
    }

    // A shooter's ring: Verb.DrawHighlight (targeting), Command_VerbTarget and PawnAttackGizmoUtility (gizmo hover),
    // Command_Ability, Verb_CastAbility all draw it through VerbProperties.DrawRadiusRing.
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.DrawRadiusRing))]
    public static class VerbProps_DrawRadiusRing_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(VerbProperties __instance, IntVec3 center, Verb verb)
        {
            if (verb?.caster == null || __instance.IsMeleeAttack || !__instance.targetable || Find.CurrentMap == null)
            {
                return;
            }
            RangePreview.Draw(verb.caster, center, verb.EffectiveRange);
        }
    }

    // A selected turret's ring (its selection overlay).
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.DrawExtraSelectionOverlays))]
    public static class TurretGun_SelectionRing_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(Building_TurretGun __instance)
        {
            Verb v = __instance.AttackVerb;
            if (v == null || v.verbProps.EffectiveMinRange(allowAdjacentShot: true) >= v.EffectiveRange)
            {
                return;
            }
            RangePreview.Draw(__instance, __instance.Position, v.EffectiveRange);
        }
    }

    // Every radius ring on a map with floors stays on the floor of its centre (GenDraw.DrawRadiusRing round an upper-floor
    // or ground cell would run on through the rows of the next floor, drawn up there): its cells are cut at that floor's
    // band, without an edge drawn along the cut (the band's own north and south edges are the map's edges there).
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawRadiusRing), new[] { typeof(IntVec3), typeof(float), typeof(Color), typeof(Func<IntVec3, bool>) })]
    public static class GenDraw_RadiusRing_Band_Patch
    {
        private static readonly List<IntVec3> cells = new List<IntVec3>();
        private static readonly HashSet<IntVec3> cut = new HashSet<IntVec3>();

        public static bool Prefix(IntVec3 center, float radius, Color color, Func<IntVec3, bool> predicate)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || radius > GenRadial.MaxRadialPatternRadius)
            {
                return true;
            }
            int k = ProjectZ.FloorOf(center, map);
            if (k < 0)
            {
                return true;
            }
            int lo = k * a.stride, hi = k * a.stride + a.groundZ - 1;
            cells.Clear();
            cut.Clear();
            int n = GenRadial.NumCellsInRadius(radius);
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = center + GenRadial.RadialPattern[i];
                if (c.z < lo || c.z > hi)
                {
                    if (c.z == lo - 1 || c.z == hi + 1)
                    {
                        cut.Add(c);
                    }
                    continue;
                }
                if (predicate == null || predicate(c))
                {
                    cells.Add(c);
                }
            }
            GenDraw.DrawFieldEdges(cells, color, null, cut.Count > 0 ? cut : null);
            return false;
        }
    }
}
