# ProjectZ Api notes

Every public call is documented where it is declared, in [`Source/Api.cs`](../Source/Api.cs). This file holds what
does not fit there: the combat parts (Api 6) with the inventory of their patches, the rules for the 3D queries,
other mods' ladders (Api 7), and floors and stairs for other mods (Api 8).

## Version checks

`ProjectZ.Api.Version` is raised whenever something is added. A member of version N is missing on an older ProjectZ:

- check `Api.Version >= N` first;
- call the member from a method of its own (marked `[MethodImpl(MethodImplOptions.NoInlining)]`), because a missing
  member fails the whole method that names it, when that method is first run.

```csharp
public MyPackMod(ModContentPack content) : base(content)
{
    if (ProjectZ.Api.Version >= 6)
    {
        KeepParts();
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
private static void KeepParts() => ProjectZ.Api.KeepCombatParts(
    ProjectZ.CombatPart.All & ~(ProjectZ.CombatPart.RangeHeightBonus | ProjectZ.CombatPart.HeightAccuracy), "Peren.IsoRimWaylith.CEPack");
```

## Combat parts (Api 6)

ProjectZ's combat between floors comes in parts (`ProjectZ.CombatPart`, flags). Two kinds of addon change them, from
their Mod constructor:

- **An addon that bridges another combat system** (today the only one ProjectZ knows is Combat Extended,
  `Api.ForeignCombatSystem`) calls `KeepCombatParts(parts, by)` for the parts that still apply with that system.
- **An addon that does a part itself** calls `ReleaseCombatParts(parts, by)`. ProjectZ's version of that part is then
  off, with or without a foreign system.

`CombatPartOn(part)` tells what runs now, once startup has settled.

### The rule

- A released part is off.
- Explosions, Shields and SkyStrikes never depended on Combat Extended: they are on.
- If a key combat patch failed (FailSoft closed the "combat" group), every other part is off.
- With no foreign combat system, every part is on.
- With a foreign combat system, only the parts an addon kept are on.

So with Combat Extended and no addon declaring anything, the state is exactly as before Api 6: explosions, shields and
sky strikes on, the rest off.

### When the patches land

- **No foreign system:** every part is patched in ProjectZ's Mod constructor, as before Api 6. A release from a later
  constructor undoes the part's patches (FailSoft `UndoParts`; the summary lists them as released, not failed). The rule
  parts are read at run time.
- **A foreign system:** the patches of the parts it turns off are deferred. They land in a
  `[StaticConstructorOnStartup]` (`CombatGateSettle`) after every Mod constructor has run, so a declaration counts whatever
  the load order. The parts it never touched are patched in the constructor as before. A failed key patch in the deferred
  group still closes the group. A `KeepCombatParts` call after that patches its parts at once. Code that ran in between
  saw those parts off.
- **Setup steps** that belong to a part (AimFloor's method list, IsoRim drawing ProjectZ's shots between floors) run
  through `CombatGate.OnSettle`. They run in the constructor without a foreign system, else at settle time.

### Rule parts

| Part | What it changes at run time |
|---|---|
| RangeHeightBonus | `Range3D.BonusPerFloor` 1 → 0. Range between floors is then the plain 3D distance; the rings on other floors and the range shell lose the downward bulge; `Api.RangeDistance` = `Api.Distance3D`. |
| HeightAccuracy | `CombatGate.HeightBonus` → 0. `Api.HeightAccuracyBonus` reports 0. The "Accuracy from higher floors" setting is hidden; its field and saved key `heightAccuracy` are kept. |

### Inventory: every combat patch and its part

Each patch class carries `[CombatPartOf(part)]`. FailSoft applies, defers and undoes it by that tag. "Run-time" rows are
checks in code that isn't a patch.

| Part | Patches (class, or entry) | Patched method | Run-time checks |
|---|---|---|---|
| **Range** | `Verb_OutOfRange_Patch` (key) | `Verb.OutOfRange` | |
| | `TurretGun_OrderAttack_Patch` | `Building_TurretGun.OrderAttack` | |
| | `FloatMenu_RangedReason_Patch` | `FloatMenuUtility.GetRangedAttackAction` | |
| | `AvoidGrid_Turret_Patch` | `AvoidGrid.PrintAvoidGridAroundTurret` | |
| **RangeHeightBonus** | (rule) | | `Range3D.BonusPerFloor` |
| **Sight** | `Verb_CanHitCell_Patch` (key) | `Verb.CanHitCellFromCellIgnoringRange` | `CombatGate.Enabled` (kept for compatibility) = Sight on |
| | `ShootLean_Patch` (key) | `ShootLeanUtility.LeanShootingSourcesFromTo` | |
| | `AttackTargetFinder_CanSee_Patch` (key) | `AttackTargetFinder.CanSee` | |
| | `ShotTip_NoSight_Patch` | `TooltipUtility.ShotCalculationTipString` | |
| **TargetPicking** | `AttackTargetFinder_Best_Patch` (key) | `AttackTargetFinder.BestAttackTarget` | |
| | `CastPositionFinder_Patch` (key) | `CastPositionFinder.TryFindCastPosition` (CastSpots) | |
| **HitReport** | `ShotReport_Patch` (key) | `ShotReport.HitReportFor` | `Api.HeightAccuracyBonus` |
| | `ShootLine_MissWild_Patch` (key) | `ShootLine.ChangeDestToMissWild` | |
| **HeightAccuracy** | (rule) | | `CombatGate.HeightBonus`, settings slider |
| **Flights** | `Projectile_Launch_Patch` (key) | `Projectile.Launch` | IsoRim `ProjectileCard` (ProjectilePitch) and `AddProjectileArc` (IsoRimBridge, an OnSettle step) |
| | `Projectile_Intercept_Patch` (key) | `Projectile.CheckForFreeIntercept` | |
| | `LobbedCast_Patch` | `Verb_LaunchProjectile.TryCastShot` | |
| **Explosions** | `DamageWorker_ExplosionCellsToHit_Patch` | `DamageWorker.ExplosionCellsToHit` | |
| | `ExplosionDistance_Patch` | the explosion's distance calls (transpiler list) | |
| **Shields** | `ProjectileInterceptor_3D_Patch` | `CompProjectileInterceptor.CheckIntercept` | |
| | `ProjectileInterceptor_GroundDistance_Patch` | `CheckBombardmentIntercept`, `BombardmentCanStartFireAt` | |
| **SkyStrikes** | `OrbitalStrike_StartStrike_Patch`, `Bombardment_NextCell_Patch`, `Bombardment_TryDoExplosion_Patch`, `Bombardment_StartRandomFire_Patch`, `PowerBeam_Fire_Patch`, `PowerBeam_Tick_Patch` | orbital strikes, bombardments, power beams | |
| | `Lightning_DoStrike_Patch`, `Lightning_FireEvent_Patch`, `LightningDelayed_Tick_Patch`, `Flashstorm_GoodLocation_Patch` | lightning, flashstorms | |
| | `ForcedMiss_Patch` | `Verb_LaunchProjectile.TryCastShot` (forced miss pick) | |
| | `Projectile_ImpactOnTop_Patch` | `Projectile.ImpactSomething` (lobbed shells on column tops) | |
| **AimFloor** | entries `AimFloor:<Type.method>` (20 rows) and `AimFloor:GenDraw.DrawAimPie` | spews, sprays, beams, foam turret, aim pie | `AimFloor.Patch` (an OnSettle step) |
| **Jumps** | `Jump_VerbFlyer_Patch`, `Jump_OrderFlyer_Patch`, `Jump_CanHitTargetFrom_Patch`, `Jump_ShootLine_Patch`, `Jump_DrawHighlight_Patch`, `AIJumpToJobTarget_Patch` | jump verbs, ordered and AI jumps | `JumpAcross.TryGive`, `FloorFlight.TryFlee`, `FloorUnstrand.TryGive` |
| | `Flee_FlyAcross_Patch` | `JobDriver_Flee.MakeNewToils` | |
| **RangePreview** | `VerbProps_DrawRadiusRing_Patch`, `TurretGun_SelectionRing_Patch` | the range rings | `RangePreview.Draw` (rings on other floors, the shell) |

"(key)" marks a key patch of the FailSoft "combat" group: if one fails, the group closes.

**Not combat parts (always on).** These are floor-safety or generic rules. They never depended on Combat Extended and an
addon can't release them:

- `Explosion_AffectCell_Patch` and `DoomsdayRocket_Impact_Patch`: nothing reaches a void row or another band.
- `Jump_ValidTarget_Patch`: no landing in void rows.
- `PawnFlyer_MakeFlyer_Patch`, `PawnFlyer_Recompute_Patch`, `PawnFlyer_Shadow_Patch`, `PawnFlyer_Destroy_Patch` and
  `PawnFlyer_CheckDestination_Patch`: flights drawn and kept between floors.
- `GenDraw_RadiusRing_Band_Patch`: any ring clipped to its band.
- `TurretTop_Aim_Patch`: guns turn toward a target on another floor.
- `AIFightEnemy_ShouldLoseTarget_Patch`.
- `PermitRange_Patch`.
- The BandRadial and Dist3D lists.

### Combat Extended

With Combat Extended and no bridging addon, ProjectZ leaves shooting between floors to CE, as before Api 6. The
"IsoRim Waylith - Combat Extended Pack" keeps `All & ~(RangeHeightBonus | HeightAccuracy)`. Range between floors is then
plain 3D distance and there is no accuracy bonus shooting down, the same rule for every shot. The vanilla-only parts
(hit report, flights, lobbed rules) stay on for the vanilla verbs and projectiles still in the game (psycasts,
abilities, unconverted mod weapons).

## 3D space queries (Api 6)

Virtual space: x and z are over the ground rows (a cell spans `[x, x + 1)`, as in map positions); y is the absolute
height in cells (floor k's floor is at `k * StoreyHeight`; negative below the ground). A height "over a floor" is in
cells over that floor's floor. `WallHeight` is IsoRim's locked wall height; `StoreyHeight` is 0.02 more.

| Call | Wraps | Notes |
|---|---|---|
| `FloorRowShift(floor, map)` | `Levels.Shift` | 0 without floors and for a floor the map doesn't have |
| `ToVirtual(mapPos, heightOverFloor, map, out v)` | `Levels.LevelOf`, `Levels.Shift` | false for a void row or rows past the top floor's; rows south of the ground rows count as the ground |
| `FromVirtual(v, map, out mapPos, out floor, out heightOverFloor)` | `Levels.Shift` | floor = `floor(y / StoreyHeight)` clamped to the map's floors; `mapPos.y` = heightOverFloor; false when x, z leave `GroundRect` (outs still set) |
| `HasFloorAt(groundCell, floor, map)` | `Column.FloorAt` | |
| `PlaneSolid(groundCell, upperFloor, map)` | `Column.PlaneSolid` | top floor + 1 = its roof |
| `CeilingSolid(cell, map)` | `!Column.OpenAbove` | a stairwell's opening is open |
| `SightBlockerAt(groundCell, floor, map)` | `Column.BlockAt` | |
| `ColumnTop(cell, map)` | `Column.Top` | |
| `LineClear3D(from, fromHeight, to, toHeight, map, validator)` | `Los3D.Clear` (Los3D's walk, `Blocked`, `PartEnd`, `FirstPlane`) | one height pair, no corner walks; any cells, also on one floor or a map without floors |
| `WalkLine3D(from, to, map, into, stopAtSolidPlane)` | `Los3D.Walk` (the same walker) | `Api.LineStep` per column; consecutive steps' height ranges meet (`PartEnd`), so a plane crossed at or beside a cell corner is reported on one step (`FirstPlane`); `into` cleared; no allocation |
| `ExplosionCells3D(center, map, radius, sameFloorCells, into, centreHeight, angle)` | `Explosions3D.CellsToHit` | adds to `into` (not cleared); centreHeight in cells (< 0: ProjectZ's default) |
| `ClipRadialLoops(method, mapPath)` | `BandRadial` (an extra row) | before ProjectZ's constructor: queued; the row syntax of `BandRadialList` |

## Other mods' ladders (Api 7)

Any ThingDef whose `thingClass` is `ProjectZ.Building_SiegeLadder` or a subclass is a ladder. It gets a normal ladder
link (`LinkKind.Ladder`, anyone may climb it, one pawn at a time) and is drawn with ProjectZ's ladder pieces; its own
sprite shows only while it has no link or 3D is off. There are no per-Def models, no custom lifts, and raiders never
place another mod's ladder. They may climb one that stands, as they climb any link.

### Defining a ladder

1. A class deriving from `Building_SiegeLadder` (the class itself will do). Call `base` in `ExposeData`, `SpawnSetup`
   and `DeSpawn`.
2. A 1x1 ThingDef with `<passability>Standable</passability>` and, for a placed ladder, `<rotatable>true</rotatable>`.
   Add `minifiedDef` if it should be uninstallable.
3. A PlaceWorker. It alone decides how the ladder is placed. `designationCategory` plays no part:

| `placeWorkers` holds | Stands on | Rotation | Link |
|---|---|---|---|
| `ProjectZ.PlaceWorker_LadderFromFoot` or a subclass | the foot | toward the top | worked out when it spawns |
| `ProjectZ.PlaceWorker_EmergencyLadder` or a subclass | the top cell | toward the open side | worked out when it spawns; can be built from either end |
| neither | wherever its code spawns it | any | only drawn: its code sets `top` and `span` (`foot` defaults to its position) before spawning, as `LadderSite.Spawn` does for raid ladders |

The lookup is cached per Def (`LadderDefs.PlacementOf`), so `placeWorkers` must not change after the game has loaded.

```xml
<ThingDef ParentName="BuildingBase">
  <defName>MyMod_RopeLadder</defName>
  <thingClass>MyMod.Building_RopeLadder</thingClass>
  <passability>Standable</passability>
  <rotatable>true</rotatable>
  <designationCategory>Structure</designationCategory>
  <placeWorkers>
    <li>ProjectZ.PlaceWorker_LadderFromFoot</li>
  </placeWorkers>
  <!-- graphicData, statBases, costList, minifiedDef, ... -->
</ThingDef>
```

### What ProjectZ does for a placed ladder

- Its blueprint, frame and install blueprint keep the shaft open, as the built ladder does (`LadderColumn`).
- The placing ghost marks the other end of the ladder.
- Each time it is newly placed, also when reinstalled, the link is worked out from where it stands. Anything already in
  `top`, `foot` and `span` is dropped, so a subclass of a placed ladder can't preset them.
- On load the saved link stays; a ladder saved without a link looks for one.
- If the site went bad between placing and finishing, the ladder stands without a link and shows its sprite.

### `LadderSite`

`LadderSite.FindFromFoot(foot, rot, map, out top, out span, out why, allowBlocker = false)` looks up from `foot` toward
`rot` and returns the lowest level with a valid top. Each candidate goes through `LadderSite.Find`, so both directions
run the same checks, and it counts only if `Find` lands on this foot. `why` is not translated and is meant for logs.

Both also have an overload ending in `Thing ignore`: a ladder that is moving, whose frame on the foot or own old link
must not count against the new site.

### What stays with the addon

ProjectZ does not touch cost, refund, work, stats or the ladder's lifetime. A Def without `CompProperties_SiegeLadder`
stands until it is destroyed. With it, it falls apart after `ticks` (`raidGate` false: counted from when it is built;
true: counted after the raid). If the ladder is destroyed while someone climbs, the climber falls.

## Floors and stairs for other mods (Api 8)

Two read-only queries. Check `Api.Version >= 8` first and call them from a `NoInlining` method of their own (see
Version checks); `StairGeometry` is missing on an older ProjectZ too.

### `TryGetFloorFor`

`TryGetFloorFor(stuff, out floor)` gives the buildable floor (slab) of that stuff. It is false with `floor` null when
the stuff has none, and for a null stuff: there is no wood fallback and it never gives a damaged slab.

### `TryGetStairGeometry`

`TryGetStairGeometry(center, rot, map, out geometry)` works for a planned position, with nothing built.
`TryGetStairGeometry(stairs, out geometry)` takes a spawned stairs thing of any state (built, blueprint, frame), ship
stairs too. Both give false and no exception for a null map or thing; the Thing overload gives false for an unspawned
thing or anything that is not stairs. On false, `geometry` is empty.

The result is shape only. The position overload is false where the shape can't stand: a map without floors, a gap row,
the top floor, a footprint cell off the floor or out of bounds, no exit. It does not ask about blockers, fog, terrain,
thick roofs or anything else that can change, so true does not mean stairs can be placed there. Placement still has the
last word (`GenConstruct.CanPlaceBlueprintAt`). Ship stairs have the same shape, but must also stand on the ship's
deck, which is not asked either: the position overload is true off a deck, where they are refused.

| `StairGeometry` field | What | Floor |
|---|---|---|
| `center`, `rot` | the stairs' position and rotation | `floor` |
| `floor` | k, the floor the stairs stand on | |
| `footprint` | the 1x4 rect: the ramp and the solid end | `floor` |
| `ramp` | the 3 ramp cells in the order low, middle, high | `floor` |
| `solid` | the solid end past the high cell, not walkable | `floor` |
| `low` | where the ramp starts | `floor` |
| `high` | the top of the ramp (`VLink.bottom` on built stairs) | `floor` |
| `exit` | where the stairs come out, over `solid` (`VLink.top` on built stairs) | `floor` + 1 |
| `well` | the ramp cells on the floor above, in ramp order: the opening kept free | `floor` + 1 |

All cells are ordinary map cells, each in the rows of its own floor. To compare cells of two floors use `GroundCellOf`.
