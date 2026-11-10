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
    // Only where the game itself drew the range ring in that call (GameRing): whether a range gets a ring (the radial
    // pattern's limit, the map's size, a turret's own limit, another mod's) is the game's answer, never a copied number.
    // The range is the weapon's (GameRange.Weapon), its 3D conversion GameRange's.
    // The mechanitor's command range gets the same rings and a white shell (Rings, RangeShell; MechanitorRange_Preview_Patch).
    public static class RangePreview
    {
        // Colour of the shell (its alpha comes from the Fresnel term) and of the added rings.
        public static readonly Color ShellColor = new Color(0.78f, 0.9f, 1f, 1f);
        public const float ShellAlphaFace = 0.035f;   // looking straight at the shell
        public const float ShellAlphaEdge = 0.3f;     // at its outline

        private static int drawnFrame = -1;
        private static readonly HashSet<long> drawnThisFrame = new HashSet<long>();

        // (tests) previews drawn: rings added, shells; the frame of the last one
        public static int Rings3D, Shells, LastFrame = -1;

        // ---- the game's own ring calls (GenDraw.DrawRadiusRing, GenDraw_RadiusRing_Seen_Patch): the radii asked since
        // a ring call site began (GameRingStart in its prefix), so its postfix knows whether the game drew that range.
        private static readonly List<float> ringsAsked = new List<float>();
        private static bool listening;

        public static void GameRingStart()
        {
            ringsAsked.Clear();
            listening = true;
        }

        public static void GameRingEnd()
        {
            listening = false;
        }

        public static void GameRingAsked(float radius)
        {
            if (listening && ringsAsked.Count < 16)
            {
                ringsAsked.Add(radius);
            }
        }

        // The first radius the game drew in the current call site (false: none).
        public static bool GameRingFirst(out float radius)
        {
            radius = ringsAsked.Count > 0 ? ringsAsked[0] : 0f;
            return ringsAsked.Count > 0;
        }

        // The game drew a ring of this radius in the current call site.
        public static bool GameRing(float radius)
        {
            for (int i = 0; i < ringsAsked.Count; i++)
            {
                if (Mathf.Abs(ringsAsked[i] - radius) < 0.001f)
                {
                    return true;
                }
            }
            return false;
        }

        // From the game's ring call sites (patches below), after the game drew the verb's range ring. caster: the
        // shooter or turret; at: the cell the ring is round.
        public static void Draw(Thing caster, IntVec3 at, Verb verb)
        {
            Map map = Find.CurrentMap;
            float range = verb == null ? 0f : GameRange.Weapon.Flat(verb);
            if (!CombatGate.On(CombatPart.RangePreview) || caster == null || map == null || caster.MapHeld != map || !at.IsValid || range <= 1.5f || !GameRing(range)
                || !View3D(map))
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
            if (!FirstThisFrame(caster, range))
            {
                return;
            }
            try
            {
                Rings3D += Rings(g, ks, k => GameRange.HorizontalReach(range, GameRange.Weapon.sphere, ks, k), Color.white, null, map);
                float muzzle = IsoRimBridge.MuzzleHeight(caster);
                GameRange.DrawShell(new Vector3(g.x + 0.5f, ks * Range3D.Storey + muzzle, g.z + 0.5f), range, GameRange.Weapon.sphere, muzzle);
                Shells++;
                LastFrame = Time.frameCount;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the range preview failed: " + e, 61190321);
            }
        }

        // The previews are added only in IsoRim's 3D view of a map with floors.
        public static bool View3D(Map map) => map != null && IsoRimBridge.Present && IsoRimBridge.View3DOn && ProjectZ.Annex(map) != null;

        // Once a frame per caster and range.
        public static bool FirstThisFrame(Thing caster, float range)
        {
            if (drawnFrame != Time.frameCount)
            {
                drawnFrame = Time.frameCount;
                drawnThisFrame.Clear();
            }
            return drawnThisFrame.Add(((long)caster.thingIDNumber << 20) ^ Mathf.RoundToInt(range * 100f));
        }

        // The cross-section rings round ground cell g of a range from floor ks: on the ground and on the viewed floor, the
        // floor ks itself keeping the game's ring. reach(k): how far over the ground the range reaches on floor k (below
        // 1: not drawn); predicate (optional): the cells inside, exactly (the ring then follows the rule cell by cell).
        // Returns the rings drawn.
        public static int Rings(IntVec3 g, int ks, Func<int, float> reach, Color color, Func<IntVec3, bool> predicate, Map map)
        {
            int view = FloorView.Current;
            int n = Ring(g, ks, 0, reach, color, predicate, map);
            if (view != 0)
            {
                n += Ring(g, ks, view, reach, color, predicate, map);
            }
            return n;
        }

        private static int Ring(IntVec3 g, int ks, int k, Func<int, float> reach, Color color, Func<IntVec3, bool> predicate, Map map)
        {
            if (k == ks)
            {
                return 0;   // the game's own ring
            }
            float r = reach(k);
            if (r < 1f)
            {
                return 0;
            }
            IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
            if (!c.IsValid)
            {
                return 0;
            }
            // (with a predicate the radius is a bound only: a cell over, so no cell the rule takes is left out)
            GenDraw.DrawRadiusRing(c, Mathf.Min(predicate == null ? r : r + 1f, GenRadial.MaxRadialPatternRadius - 0.5f), color, predicate);
            return 1;
        }
    }

    // A shooter's ring: Verb.DrawHighlight (targeting), Command_VerbTarget and PawnAttackGizmoUtility (gizmo hover),
    // Command_Ability, Verb_CastAbility all draw it through VerbProperties.DrawRadiusRing.
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.DrawRadiusRing))]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.RangePreview)]
    public static class VerbProps_DrawRadiusRing_Patch
    {
        public static void Prefix()
        {
            RangePreview.GameRingStart();
        }

        public static void Postfix(VerbProperties __instance, IntVec3 center, Verb verb)
        {
            try
            {
                if (verb?.caster == null || __instance.IsMeleeAttack || !__instance.targetable || Find.CurrentMap == null)
                {
                    return;
                }
                RangePreview.Draw(verb.caster, center, verb);
            }
            finally
            {
                RangePreview.GameRingEnd();
            }
        }
    }

    // A selected turret's ring (its selection overlay).
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.DrawExtraSelectionOverlays))]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.RangePreview)]
    public static class TurretGun_SelectionRing_Patch
    {
        public static void Prefix()
        {
            RangePreview.GameRingStart();
        }

        public static void Postfix(Building_TurretGun __instance)
        {
            try
            {
                Verb v = __instance.AttackVerb;
                if (v != null)
                {
                    RangePreview.Draw(__instance, __instance.Position, v);   // (only when the game drew the ring: GameRing)
                }
            }
            finally
            {
                RangePreview.GameRingEnd();
            }
        }
    }

    // The radii of the game's own ring calls (first of every prefix: seen even when another prefix skips the drawing),
    // for the call sites that follow the game's ring (RangePreview.GameRing, GameRingFirst: the weapon rings above, the
    // facility ring in ShipRange). Not a combat part: a recorder only, also for the facility ring.
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawRadiusRing), new[] { typeof(IntVec3), typeof(float), typeof(Color), typeof(Func<IntVec3, bool>) })]
    public static class GenDraw_RadiusRing_Seen_Patch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(float radius)
        {
            RangePreview.GameRingAsked(radius);
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
            int lo = Levels.RowMin(map, k), hi = Levels.RowMax(map, k);
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
