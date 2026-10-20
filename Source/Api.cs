using System;
using ProjectZ.Combat;
using Verse;

namespace ProjectZ
{
    // ProjectZ's public API for other mods and addons. Call from the addon's own code (no reflection needed when it
    // references IsoRimProjectZ.dll; the names below are stable).
    public static class Api
    {
        // ---- lobbed shots (indirect fire) between floors

        // Registers a rule that declares how a shot between two floors flies:
        //   rule(verb, from, to): from = the shooter's cell (or the lean cell it shoots from), to = the target's cell; the
        //   two are always on different floors of a map with floors (ProjectZ never asks about shots on one floor).
        //   Return true: lobbed (indirect fire, like a mortar shell): no line of sight is needed to see, pick and hit the
        //     target, and the projectile ignores everything between, then comes down on the top of the target's column
        //     (a slab or floor over the target stops it there; a vanilla roof as the game does for shells).
        //   Return false: direct: line of sight needed, flies as a direct shot between floors (also when its projectile
        //     def has flyOverhead = true: it does not fall onto a slab top).
        //   Return null: no say (the next rule; with none, ProjectZ's default: the verb's requireLineOfSight and the
        //     projectile def's flyOverhead).
        // The rule is asked often (sight and hit checks in target searches): keep it cheap. Rules are asked in the order
        // registered; the first answer wins. Register once (e.g. from a StaticConstructorOnStartup).
        public static void RegisterLobbedShot(Func<Verb, IntVec3, IntVec3, bool?> rule) => Lobbed.Register(rule);

        public static void UnregisterLobbedShot(Func<Verb, IntVec3, IntVec3, bool?> rule) => Lobbed.Unregister(rule);

        // What the rules declare for this shot between floors (true lobbed, false direct, null nothing declared).
        public static bool? DeclaredLobbed(Verb verb, IntVec3 from, IntVec3 to) => Lobbed.Declared(verb, from, to);

        // Marks one projectile as lobbed (true) or direct (false) between floors, overriding the rules for it. A
        // projectile launched by Verb_LaunchProjectile.TryCastShot is marked from the rules at launch; call this for one
        // launched any other way, before Projectile.Launch (or right after it, before its first tick; then the flight is
        // not redone: call it before for a direct shot of a flyOverhead def).
        public static void SetLobbed(Projectile projectile, bool lobbed) => Lobbed.Mark(projectile, lobbed);

        // Does this projectile fly as a lobbed shot in ProjectZ (its mark, else its def's flyOverhead)?
        public static bool IsLobbed(Projectile projectile) => Lobbed.Of(projectile);

        // ---- helpers for rules

        // The two cells are on different floors of a map with floors.
        public static bool OnDifferentFloors(IntVec3 a, IntVec3 b, Map map) => map != null && Heights.CrossFloor(a, b, map);

        // ProjectZ's line of sight between two cells on different floors (walls, slabs and roofs between, in 3D).
        public static bool CanSeeBetweenFloors(IntVec3 from, IntVec3 to, Map map) => map != null && Los3D.CanSee(from, to, map);

        // Open sky over the cell: no roof over it (a stairwell's opening counts as open) and no floor built above it.
        public static bool OpenSkyOver(IntVec3 c, Map map)
        {
            if (map == null || !c.InBounds(map))
            {
                return false;
            }
            if (!Column.OpenAbove(c, map))
            {
                return false;
            }
            int k = ProjectZ.FloorOf(c, map);
            if (k < 0)
            {
                return !c.Roofed(map);
            }
            IntVec3 top = Column.Top(c, map);
            return !top.IsValid || ProjectZ.FloorOf(top, map) <= k;
        }
    }
}
