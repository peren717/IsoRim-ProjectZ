using System;
using System.Collections.Generic;
using ProjectZ.Combat;
using Verse;

namespace ProjectZ
{
    // ProjectZ's public API for other mods and addons. Call from the addon's own code (no reflection needed when it
    // references IsoRimProjectZ.dll; the names below are stable).
    public static class Api
    {
        // The API version, raised whenever something is added here, so an addon can check for what it needs
        // (1: lobbed shots between floors, OnDifferentFloors, CanSeeBetweenFloors, OpenSkyOver;
        //  2: floor queries, the height rule (FitsUnderFloor, RegisterHeightRule, TooTallUnderFloor), events (FloorChanged,
        //     PawnChangedFloor, AddStepHook), combat queries (Distance3D, RangeDistance, HeightAccuracyBonus);
        //  3: stacked cells (StackedCell, StackedCells) and vertical links drawn like power risers (AddRiserSource,
        //     RemoveRiserSource, RiserSource, RiserLink)).
        // A member of version N is missing on an older ProjectZ: check Version >= N first, and call it from a method of its
        // own (a missing member fails the whole method that names it, when that method is first run).
        public static int Version => 3;

        // ---- floors (2)
        // Floors are numbered from the ground: 0 = the ground, 1 = the floor above it, and so on. On a map without floors
        // (ProjectZ off for that map, or a world made with one floor) every cell is on floor 0. In the map's cell grid each
        // floor is a block of rows north of the ground (a "band", the same width and the ground's height), with a few void
        // rows between two bands that are on no floor (FloorOf -1). Cells of the floors are ordinary map cells: things,
        // terrain, roofs and pawns on them use the game's usual calls.

        // The map has floors above the ground.
        public static bool HasFloors(Map map) => map != null && ProjectZ.HasAnnex(map);

        // How many floors the map has, the ground counted (1 without floors).
        public static int FloorCount(Map map) => map == null ? 1 : Levels.Count(map);

        // The floor of a cell: 0 the ground, k floor k; -1 for a void row between two floors. Cheap (a row compare).
        public static int FloorOf(IntVec3 c, Map map) => map == null ? 0 : ProjectZ.FloorOf(c, map);

        // The floor a thing is on (where it is held: a carried or stored thing, its holder's); -1 when it is on no map.
        public static int FloorOf(Thing t)
        {
            Map map = t?.MapHeld;
            return map == null ? -1 : ProjectZ.FloorOf(t.PositionHeld, map);
        }

        // The ground cell under (or at) c: the same x and z over the ground. Invalid for a void row cell.
        public static IntVec3 GroundCellOf(IntVec3 c, Map map) => map == null ? c : ProjectZ.GroundCellOf(c, map);

        // Ground cell g lifted to floor k (k = 0 gives g). Invalid when the map has no floor k or g is no ground cell.
        public static IntVec3 CellOnFloor(IntVec3 ground, int floor, Map map) => map == null ? (floor == 0 ? ground : IntVec3.Invalid) : ProjectZ.CellOnFloor(ground, floor, map);

        // The two cells are on the same floor (always on a map without floors; a void row cell is on none).
        public static bool SameFloor(IntVec3 a, IntVec3 b, Map map) => map == null || ProjectZ.SameBand(a, b, map);

        // The ground's cells (the whole map without floors): ground cell g is on every floor k at CellOnFloor(g, k, map).
        public static CellRect GroundRect(Map map) => ProjectZ.GroundRect(map);

        // One floor's height in cells, as drawn and as used for 3D distances (a little over IsoRim's wall height).
        public static float StoreyHeight => IsoRimBridge.StoreyHeight;

        // The floor the player views and works on in the current map (the floor HUD; 0 without floors). Not saved.
        public static int ViewedFloor => FloorView.Current;

        // Views floor k of the current map (clamped to the map's floors), as the floor keys do.
        public static void SetViewedFloor(int floor) => FloorView.Set(floor);

        // ---- stacked cells (3)
        // The column rule other mods' nets can use to join across floors (the rule power conduits use: a conduit touches a
        // conduit in the stacked cell directly above or below it).

        // The cell directly above (up) or below c, in the same column on the next floor; IntVec3.Invalid when there is no
        // such floor, for a void-row cell, and on a map without floors.
        public static IntVec3 StackedCell(IntVec3 c, Map map, bool up)
        {
            if (map == null || !c.IsValid || !c.InBounds(map) || !ProjectZ.HasAnnex(map))
            {
                return IntVec3.Invalid;
            }
            IntVec3 s = up ? StairShape.Above(c, map) : FloorRoof.Below(c, map);
            return s.IsValid && s.InBounds(map) ? s : IntVec3.Invalid;
        }

        // Adds the valid stacked cells of c to 'into' (above first, then below), allocation free; returns how many it added.
        public static int StackedCells(IntVec3 c, Map map, List<IntVec3> into)
        {
            int n = 0;
            IntVec3 up = StackedCell(c, map, true);
            if (up.IsValid)
            {
                into.Add(up);
                n++;
            }
            IntVec3 down = StackedCell(c, map, false);
            if (down.IsValid)
            {
                into.Add(down);
                n++;
            }
            return n;
        }

        // ---- vertical links drawn like power risers (3)
        // An addon that joins its own net across floors lists the vertical links (RiserSource, RiserLink in ApiRisers.cs)
        // and ProjectZ draws each one like a power riser in IsoRim's 3D view: a short upright piece in the link's colour
        // from the lower cell up through the ceiling, lit like IsoRim's models (none on an end the game draws invisible);
        // while the source's overlay is shown, its overlay line stood up between the two ends and its end mark at each
        // end. Hidden floors are handled as for power (a half-storey stub while the upper floor is hidden). Nothing is
        // drawn in the 2D view. Power is the first source, built in. Add once (e.g. from a StaticConstructorOnStartup).
        public static void AddRiserSource(RiserSource source) => RiserView.Add(source);

        public static void RemoveRiserSource(RiserSource source) => RiserView.Remove(source);

        // ---- the height rule under floors (2)
        // A building drawn taller than a storey's head room can't have a floor over it, and can't be placed under one
        // (refused with a message). ProjectZ measures IsoRim's model height; walls, doors, stairs and its elevator never
        // count. A tall model that should fit anyway (glass, a sign that reaches into the floor above):
        //   XML: <modExtensions><li Class="ProjectZ.FitsUnderFloor" /></modExtensions> on the ThingDef (never too tall), or
        //   code: a rule, below.

        // Registers a rule for the height rule: rule(def) returns true (too tall: no floor over it), false (fits) or null (no
        // say: the next rule, then the model height). Asked for buildings only, after FitsUnderFloor and ProjectZ's own
        // exceptions; the first answer wins. Asked on every placement and floor check: keep it cheap. Register once.
        public static void RegisterHeightRule(Func<BuildableDef, bool?> rule) => HeightRule.Register(rule);

        public static void UnregisterHeightRule(Func<BuildableDef, bool?> rule) => HeightRule.Unregister(rule);

        // ProjectZ's answer for a def: too tall for a floor over it (with the extension and the rules applied).
        public static bool TooTallUnderFloor(BuildableDef def) => HeightRule.TooTall(def);

        // ---- events (2)
        // Subscribe once (e.g. from a StaticConstructorOnStartup); handlers run on the main thread, in the middle of the
        // game's own work: keep them short, and don't spawn, despawn or move things from them. A handler that throws is
        // logged once and skipped. With nobody subscribed they cost nothing worth measuring.

        // A floor was built or removed on a cell of an upper floor: (map, cell, built). built = true: the cell turned from
        // open air into a floor (a slab built, a ship deck landed, stairs' landing laid); false: back to open air (taken up,
        // collapsed, a ship took off). Not fired while the map is being generated, nor for a floor covering changed on a
        // floor (a carpet on a slab).
        public static event Action<Map, IntVec3, bool> FloorChanged
        {
            add => ApiEvents.FloorChanged += value;
            remove => ApiEvents.FloorChanged -= value;
        }

        // A spawned pawn moved to another floor of its map: (pawn, from floor, to floor). Fired right after its position
        // changed, for every cause (stairs, ladders and elevators at the end of the step, jumps, falls, teleports). Not
        // fired when a pawn spawns or despawns (a carried pawn set down on another floor: it spawns there).
        public static event Action<Pawn, int, int> PawnChangedFloor
        {
            add => ApiEvents.PawnChangedFloor += value;
            remove => ApiEvents.PawnChangedFloor -= value;
        }

        // Adds a hook asked right before a pawn starts a stair, ladder or elevator step: hook(pawn, kind, link): kind
        // (ProjectZ.StepKind) says which: StairUp/Down (also ship stairs), LadderUp/Down (the emergency ladder, the
        // raiders' ladders, the mechanoids' lift), ElevatorUp/Down (the elevator; without power it is climbed like a
        // ladder); link is the
        // stairs, ladder or elevator building; the pawn stands on the step's start (pawn.Position), its end is
        // pawn.pather.nextCell. Return true to let the step go on; the hook may act first (a rider gets off its mount).
        // Return false to refuse it: the pawn stops where it is and its path fails (its job ends and it thinks again, as
        // when a ladder is gone). Refuse only for a reason that goes away, or the pawn keeps trying. Every hook is asked;
        // one false refuses. Asked each time a step starts: once per step, again when a step is started over (the pawn's
        // path renewed mid-step), and also for a pawn that then waits for a ladder held by another.
        public static void AddStepHook(Func<Pawn, StepKind, Building, bool> hook) => ApiEvents.AddStepHook(hook);

        public static void RemoveStepHook(Func<Pawn, StepKind, Building, bool> hook) => ApiEvents.RemoveStepHook(hook);

        // ---- combat between floors (2)
        // ProjectZ's own rules (off with Combat Extended loaded: it has its own heights). On one floor they are the game's.

        // True 3D distance between two cells, each on its floor: over the ground, and StoreyHeight per floor between.
        public static float Distance3D(IntVec3 a, IntVec3 b, Map map) => map == null ? (a - b).LengthHorizontal : Range3D.Distance(a, b, map);

        // The distance ProjectZ compares with a weapon's range for a shot from shooter to target: Distance3D less 1 cell
        // per floor the shooter stands above the target (shooting down reaches farther); Distance3D otherwise. A shot is in
        // range when this is at most the verb's range (and Distance3D at least its minimum range).
        public static float RangeDistance(IntVec3 shooter, IntVec3 target, Map map) => map == null ? (shooter - target).LengthHorizontal : Range3D.RangeDistance(shooter, target, map);

        // The accuracy the height gives this shot: what ProjectZ adds to the hit chance's shooter factor (the hit report's
        // "shooter" line; the bonus setting, less what the 100 % cap cuts off) when the caster stands on a higher floor than
        // the target. 0 on one floor, shooting up, for a verb that can't miss (canGoWild false), or with combat off.
        public static float HeightAccuracyBonus(Verb verb, LocalTargetInfo target)
        {
            Thing caster = verb?.caster;
            Map map = caster?.Map;
            if (map == null || !target.IsValid || !CombatGate.Enabled || !Heights.CrossFloor(caster.Position, target.Cell, map))
            {
                return 0f;
            }
            CombatGate.ShooterFactor(caster, verb, target.Cell, Heights.GroundDistance(caster.Position, target.Cell, map), map, out float bonus);
            return bonus;
        }

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
