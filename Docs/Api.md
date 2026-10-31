# ProjectZ Api notes

Every public call is documented where it is declared, in [`Source/Api.cs`](../Source/Api.cs). This file holds what
does not fit there: the combat parts (Api 6) with the inventory of their patches, and the rules for the 3D queries.

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
