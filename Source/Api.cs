using System;
using System.Collections.Generic;
using ProjectZ.Combat;
using RimWorld;
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
        //     RemoveRiserSource, RiserSource, RiserLink);
        //  4: AimPoint (turrets that turn themselves), gravship cells relative to a ship (ShipToMap, MapToShip),
        //     RequestFogRefresh, RegisterDeckTerrain; 5: ShipCells, the ship's cells on every floor without the substructure limit;
        //  6: combat parts (CombatPart, ForeignCombatSystem, KeepCombatParts, ReleaseCombatParts, CombatPartOn) and 3D space
        //     queries (WallHeight, FloorRowShift, ToVirtual, FromVirtual, HasFloorAt, PlaneSolid, CeilingSolid, SightBlockerAt,
        //     ColumnTop, LineClear3D, WalkLine3D, ExplosionCells3D, ClipRadialLoops)).
        // A member of version N is missing on an older ProjectZ: check Version >= N first, and call it from a method of its
        // own (a missing member fails the whole method that names it, when that method is first run).
        public static int Version => 6;

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

        // ---- gravships, fog and deck foundations (4)
        // A gravship stores its cells relative to the engine: the ground column offset in x and z, the floor in y (an upper
        // deck's cell keeps its floor). Another mod that keeps its own per-cell gravship data (captured at take-off, written
        // back at landing) uses these instead of "cell - origin" and "root + offset", which would land on the ground cell.
        // The offset of map cell `cell` from the ship's origin cell (both raw map cells).
        public static IntVec3 MapToShip(IntVec3 cell, IntVec3 origin, Map map) => ShipLocal.From(cell, origin, map);

        // The map cell of a ship offset `local` placed at `root` (a ground cell): its column, lifted to the offset's floor.
        public static IntVec3 ShipToMap(IntVec3 root, IntVec3 local, Map map) => ShipLocal.To(root, local, map);

        // Re-run ProjectZ's fog rule (what player pawns reach or see on every floor is revealed) on the map soon, batched:
        // for a mod that rewrote the fog over the whole map (fogged it again and unfogged from one spot).
        public static void RequestFogRefresh(Map map) => ReachFog.Request(map);

        // Another foundation that counts as a ship upper deck. On an upper floor it is built like the deck itself: directly
        // on the floor support over open air, right above the ship (the deck's own placement rules), as an alternative to
        // the deck (or in place of a deck when its own rules allow replacing substructure on the ground; never stacked); its
        // terrain affordance need is met there by the floor support. ProjectZ then treats it as
        // a deck: it carries what stands on it, flies with the ship, falls without the ship below it, is removed as a deck.
        // On the ground nothing changes (its own rules). Register once at startup (e.g. a mod's subarmor, a damaged deck).
        public static void RegisterDeckTerrain(TerrainDef def) => ShipDeck.Register(def);

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
        // ProjectZ's own rules. On one floor they are the game's. With Combat Extended loaded they answer as ProjectZ's combat
        // parts stand (6): RangeDistance has the per-floor bonus only while RangeHeightBonus is on, HeightAccuracyBonus is 0
        // while HitReport or HeightAccuracy is off; Distance3D is plain geometry.

        // True 3D distance between two cells, each on its floor: over the ground, and StoreyHeight per floor between.
        public static float Distance3D(IntVec3 a, IntVec3 b, Map map) => map == null ? (a - b).LengthHorizontal : Range3D.Distance(a, b, map);

        // The distance ProjectZ compares with a weapon's range for a shot from shooter to target: Distance3D less 1 cell
        // per floor the shooter stands above the target (shooting down reaches farther; only while the RangeHeightBonus
        // combat part is on); Distance3D otherwise. A shot is in
        // range when this is at most the verb's range (and Distance3D at least its minimum range).
        public static float RangeDistance(IntVec3 shooter, IntVec3 target, Map map) => map == null ? (shooter - target).LengthHorizontal : Range3D.RangeDistance(shooter, target, map);

        // The accuracy the height gives this shot: what ProjectZ adds to the hit chance's shooter factor (the hit report's
        // "shooter" line; the bonus setting, less what the 100 % cap cuts off) when the caster stands on a higher floor than
        // the target. 0 on one floor, shooting up, for a verb that can't miss (canGoWild false), or with combat off.
        public static float HeightAccuracyBonus(Verb verb, LocalTargetInfo target)
        {
            Thing caster = verb?.caster;
            Map map = caster?.Map;
            if (map == null || !target.IsValid || !CombatGate.On(CombatPart.HitReport | CombatPart.HeightAccuracy) || !Heights.CrossFloor(caster.Position, target.Cell, map))
            {
                return 0f;
            }
            CombatGate.ShooterFactor(caster, verb, target.Cell, Heights.GroundDistance(caster.Position, target.Cell, map), map, out float bonus);
            return bonus;
        }

        // ---- lobbed shots (indirect fire) between floors

        // Registers a rule that declares how a shot between two floors flies:
        //   rule(verb, from, to): from = the shooter's cell (or the lean cell it shoots from), to = the target's cell. Sight
        //   and hit checks ask only about shots between two floors of a map with floors; a projectile that flies overhead
        //   (flyOverhead) is also asked about at launch and for its forced miss on one floor, so a rule can declare such a
        //   weapon direct everywhere (its miss stays on the target's floor, it never comes down on a slab top).
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

        // ---- turrets that turn themselves (4)

        // Where a shooter should aim, in its own floor's frame: the flat angle from shooter.DrawPos to this point
        // (Vector3.AngleFlat of the difference) is the true direction to the target, also on another floor. On the same
        // floor, or on a map without floors, it is the target cell's centre (the game's own aim). For a turret that turns
        // itself (another mod skipped TurretTop.TurretTopTick for it, so ProjectZ leaves its gun alone): turn toward this
        // point instead of the target cell, at your own speed.
        public static UnityEngine.Vector3 AimPoint(Thing shooter, LocalTargetInfo target) => Patches.TurretTop_Aim_Patch.AimPoint(shooter, target);

        // ---- the ship's cells (5)

        // Every substructure cell connected to the engine, on every floor (decks and stairs as ProjectZ joins them), with no
        // substructure limit: what the engine's valid set would be with unlimited support. Into `into` (cleared first). For a
        // mod that counts its own cells against the limit (e.g. a foundation it leaves out of the count) on upper decks too.
        // On a map without floors: the game's own flood fill. Main thread.
        public static void ShipCells(Building_GravEngine engine, HashSet<IntVec3> into, bool requireInsideFootprint = true)
        {
            into.Clear();
            if (engine == null || !engine.Spawned)
            {
                return;
            }
            if (ProjectZ.Annex(engine.Map) == null || ShipSet.Off)
            {
                GravshipUtility.GetConnectedSubstructure(engine, into, int.MaxValue, requireInsideFootprint);
                return;
            }
            ShipSet.Fill(engine, into, int.MaxValue, requireInsideFootprint);
        }
        // ---- combat parts (6)
        // ProjectZ's combat between floors comes in parts (CombatPart; which patch belongs to which part: Docs/Api.md). An
        // addon that bridges another combat system keeps the parts that still apply with it; an addon that does a part
        // itself releases it. Call both from the addon's Mod constructor, after checking Version >= 6 (in a method of its
        // own): every Mod constructor has run before the parts are settled, whatever the load order.
        //   - No foreign combat system: every part is on and patched as before (a release turns its part off: the rule
        //     parts at once, a part's patches are undone).
        //   - A foreign combat system and no declaration: the parts it never touched (Explosions, Shields, SkyStrikes) are
        //     on, every other part is off, as before Api 6.
        //   - A foreign combat system and KeepCombatParts: those parts too are on, patched once startup has settled (a
        //     StaticConstructorOnStartup). A later call still patches them, but code that ran before saw them off.

        // Another combat system is loaded that ProjectZ can't run under alone (today: Combat Extended).
        public static bool ForeignCombatSystem => CombatGate.Foreign;

        // Keeps these parts on with a foreign combat system (no effect without one). 'by': the addon (packageId), for the log.
        public static void KeepCombatParts(CombatPart parts, string by) => CombatGate.Keep(parts, by);

        // Turns these parts off, with or without a foreign system: ProjectZ's version is left to the addon. Rule parts
        // (RangeHeightBonus, HeightAccuracy) are read at run time; a part's patches are undone if they landed already.
        public static void ReleaseCombatParts(CombatPart parts, string by) => CombatGate.Release(parts, by);

        // Every flag of 'part' runs now (tests, other addons). Valid once startup has settled (before that, with a foreign
        // system, false for the parts it turns off).
        public static bool CombatPartOn(CombatPart part) => CombatGate.On(part);

        // ---- 3D space (6)
        // Virtual space: x, z over the ground rows (a cell spans [x, x + 1) as in map positions), y = absolute height in
        // cells (floor k's floor at k * StoreyHeight; negative below the ground). Heights "over a floor" are in cells over
        // that floor's floor. On a map without floors there is one floor (0) and the map's rows are the ground rows.

        // IsoRim's locked wall height in cells (StoreyHeight is a little more), for converting another mod's units.
        public static float WallHeight => IsoRimBridge.WallHeight;

        // Rows from the ground rows to floor's rows: ground row z is row z + FloorRowShift(k) on floor k (0 without
        // floors, and for a floor the map doesn't have).
        public static int FloorRowShift(int floor, Map map) => map == null || !Levels.Has(map, floor) ? 0 : Levels.Shift(map, floor);

        // A map position on its floor (its row picks the floor) with a height over that floor, as a virtual point. False
        // (v zero) for a void row between floors, rows past the top floor's, and without a map. Rows below the ground rows'
        // end (south of the map too) count as the ground; x is not checked.
        public static bool ToVirtual(UnityEngine.Vector3 mapPos, float heightOverFloor, Map map, out UnityEngine.Vector3 v)
        {
            v = default;
            if (map == null)
            {
                return false;
            }
            int k = Levels.LevelOf(new IntVec3(0, 0, UnityEngine.Mathf.FloorToInt(mapPos.z)), map);
            if (k == Levels.None || !Levels.Has(map, k))
            {
                return false;
            }
            v = new UnityEngine.Vector3(mapPos.x, k * StoreyHeight + heightOverFloor, mapPos.z - Levels.Shift(map, k));
            return true;
        }

        // A virtual point back on the map: floor = the storey its height is in (floor(y / StoreyHeight), clamped to the
        // map's floors: above the top floor the top floor, below the lowest the lowest), heightOverFloor = y less that
        // floor's height (may be negative or over a storey when clamped), mapPos = (x, heightOverFloor, z + FloorRowShift).
        // The outs are always set; false when x or z leave the ground rows (GroundRect).
        public static bool FromVirtual(UnityEngine.Vector3 v, Map map, out UnityEngine.Vector3 mapPos, out int floor, out float heightOverFloor)
        {
            mapPos = default;
            floor = 0;
            heightOverFloor = v.y;
            if (map == null)
            {
                return false;
            }
            floor = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.FloorToInt(v.y / StoreyHeight), Levels.Min(map), Levels.Max(map));
            heightOverFloor = v.y - floor * StoreyHeight;
            mapPos = new UnityEngine.Vector3(v.x, heightOverFloor, v.z + Levels.Shift(map, floor));
            CellRect g = ProjectZ.GroundRect(map);
            return v.x >= 0f && v.z >= 0f && v.x < g.maxX + 1 && v.z < g.maxZ + 1;
        }

        // Floor 'floor' has a floor to stand on over ground cell g (slab, deck, rock floor; the ground always). False for a
        // floor the map doesn't have or g off the ground rows.
        public static bool HasFloorAt(IntVec3 groundCell, int floor, Map map) => OnGround(groundCell, map) && Levels.Has(map, floor) && Column.FloorAt(groundCell, floor, map);

        // The plane between floor upperFloor - 1 and upperFloor over ground cell g is solid: a roof over the cell below
        // (not a stairwell's opening) or a floor on upperFloor. upperFloor = the top floor + 1: the top floor's roof.
        public static bool PlaneSolid(IntVec3 groundCell, int upperFloor, Map map) => OnGround(groundCell, map) && Column.PlaneSolid(groundCell, upperFloor, map);

        // The cell (any floor) has a roof that stops things over it: false for none and for a stairwell's opening.
        public static bool CeilingSolid(IntVec3 cell, Map map) => map != null && cell.InBounds(map) && !Column.OpenAbove(cell, map);

        // A sight-blocking building (wall, closed door, rock) on floor 'floor' over ground cell g (the game's
        // CanBeSeenOverFast); it stands WallHeight high over that floor.
        public static bool SightBlockerAt(IntVec3 groundCell, int floor, Map map) => OnGround(groundCell, map) && Column.BlockAt(groundCell, floor, map);

        // The top of the column over a cell (any floor): the highest floor with a floor there, else the ground cell (the
        // cell itself without floors; Invalid for a void row). Where things falling from the sky land.
        public static IntVec3 ColumnTop(IntVec3 cell, Map map) => map == null ? IntVec3.Invalid : Column.Top(cell, map);

        // Line of sight from 'from' at fromHeight to 'to' at toHeight (cells over each end's floor): CanSeeBetweenFloors'
        // blocks (solid planes crossed anywhere, ends included: an end exactly at a floor's level crosses that plane when
        // the line comes up to it or leaves it downward; sight blockers the line passes below the top of, ends
        // exempt; the validator on the cell the line's middle is in per inner column, false = blocked). Any two cells,
        // also on one floor and on a map without floors. False for a void row end.
        public static bool LineClear3D(IntVec3 from, float fromHeight, IntVec3 to, float toHeight, Map map, Func<IntVec3, bool> validator = null)
            => map != null && Los3D.Clear(from, fromHeight, to, toHeight, map, validator);

        // One column of WalkLine3D.
        public struct LineStep
        {
            public IntVec3 cell;     // the column's cell on the floor the line's middle is on there (clamped to the map's floors)
            public int floor;        // that floor
            public float lo, hi;     // the line's height range in this column, over that floor's floor (cells);
                                     // consecutive steps' ranges meet, also where the line passes a cell corner
            public int solidPlane;   // the first solid plane crossed in this column in the line's direction (PlaneSolid's
                                     // upperFloor), int.MinValue: none
        }

        // The game's sight walk over the ground columns from virtual point 'from' to 'to' (the end cells' cell sequence),
        // one step per column. 'into' is cleared first, nothing is allocated; returns the step count (0 when an end leaves
        // the ground rows). stopAtSolidPlane: the walk ends with the column of the first solid plane crossed.
        public static int WalkLine3D(UnityEngine.Vector3 from, UnityEngine.Vector3 to, Map map, List<LineStep> into, bool stopAtSolidPlane = true)
        {
            if (into == null)
            {
                return 0;
            }
            if (map == null)
            {
                into.Clear();
                return 0;
            }
            return Los3D.Walk(from, to, map, into, stopAtSolidPlane);
        }

        // ProjectZ's explosion cell picker as a service: sameFloorCells (the other system's own cells round center) kept
        // where they are on center's floor, plus the other floors' cells within the 3D radius whose height line from the
        // centre is clear (slabs, roofs and walls block; on an upper floor only cells with a floor). Added to 'into' (not
        // cleared). centreHeight: cells over the centre's floor (< 0: ProjectZ's own, half a wall). angle: the game's
        // directional filter over the ground. Without floors: sameFloorCells as they are; a void-row centre: nothing.
        public static void ExplosionCells3D(IntVec3 center, Map map, float radius, IEnumerable<IntVec3> sameFloorCells, List<IntVec3> into,
            float centreHeight = -1f, FloatRange? angle = null)
        {
            if (map == null || into == null || sameFloorCells == null)
            {
                return;
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null)
            {
                into.AddRange(sameFloorCells);
                return;
            }
            Explosions3D.CellsToHit(sameFloorCells, center, map, radius, angle, an, into, centreHeight < 0f ? Heights.Middle : centreHeight / IsoRimBridge.WallHeight);
        }

        // ProjectZ's radius clip (the swap its vanilla list gets) for another mod's method: its radius loops
        // (GenRadial.RadialCellsAround, RadialPattern[i] + c) stay on the centre's floor; a cell past the floor's edge
        // becomes the inert void-row cell next to it. mapPath: where the method's map is read at entry: "this" (a Thing,
        // comp, verb, Map...), "arg:<parameter>", "this.<field or property>..." or "current". Call from a Mod constructor
        // or StaticConstructorOnStartup. False: no method body, the clip is off, or the patch failed (logged).
        public static bool ClipRadialLoops(System.Reflection.MethodBase method, string mapPath) => BandRadial.AddExternal(method, mapPath);

        private static bool OnGround(IntVec3 g, Map map) => map != null && g.x >= 0 && g.z >= 0 && g.x < map.Size.x && g.z < ProjectZ.GroundRect(map).maxZ + 1;
    }
}
