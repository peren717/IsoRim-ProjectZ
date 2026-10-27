# IsoRimZ

Stacked floors on one map for [IsoRim Waylith](https://github.com/peren717/IsoRim-1.7) (RimWorld 1.7): upper floors, stairs, tall mountains and multi-deck gravships, all drawn in IsoRim's 3D view.

Requires Harmony and IsoRim Waylith. Load IsoRimZ after IsoRim Waylith.

> **Once a save has used IsoRimZ, removing the mod breaks that save.**
> Floors are set per world: start a new world to get upper floors. Saves started without IsoRimZ get none.

## Floors per world

- On the Create World page, pick how many storeys every map of that world has (1 to 13, the ground included). The mod setting is only the page's starting value; the number never changes for that game.
- Also on that page: **Tall mountains** (off by default) lets mountains rise into the upper floors.

## Building upward

- Lay upper floors of wood, stone or metal over roofed rooms. An upper floor is the roof of the room below; it needs a wall or column below within reach.
- Heat passes between floors through the shared floor, like through a wall.
- Some buildings only work on the ground (drills, scanners, graves, bridges, gravship substructure). The list is XML; other mods can add to it.
- **Remove upper floor** takes floor cells up again, starting with the cells farthest from the stairs, so nobody gets cut off.

## Ways between floors

- **Stairs** (1x4: a 3-cell ramp and a solid end), built of wood, stone or metal. The open stairwell above them is kept free.
- **Improvised ladders**: free, put up from the edge of an upper floor in a second, gone after a day.
- **Elevator** (research Machining, 100 steel and 4 components, 150 W): a 1x1 lift that stops at every floor above. Without power it is climbed like a ladder.
- **Ship stairs** for gravships.
- Pawns path between floors over all of these; jumping down is never used as a path.
- Animals never climb ladders; they need stairs.

## Viewing floors

- Pick the floor you look at with the floor buttons (bottom right; Alt + drag to move them), the `=` and `-` keys, or **Shift + mouse wheel**.
- Floors above the viewed one are hidden; the walls of the viewed floor are cut away so rooms stay readable.
- **Peek** key (no default key, set it in the key bindings): while held, the floors above are drawn faded.
- Fog: every place your pawns can reach is revealed, and from every edge they can see down.
- Sun and moon shadows still fall on the ground below while an upper floor is viewed.
- On a map with floors the 3D view stays on and the wall height is fixed.

## Raids and the vertical axis

- Raiders climb **siege ladders**; mechanoids raise **lifts**. Both fall apart a day after the raid.
- Sappers and breachers that can't reach your base on foot mine up into mountains, break through walls at a ladder's top, or blast through floors.
- Drop pods, bombardments and anything else from the sky hit the current top of each column they fall on.

## Combat across floors

- Shoot up and down between floors from the edge of a floor, with an accuracy bonus when shooting down (setting, default +0.2).
- Range, line of sight and explosions are measured in 3D: walls, floors and roofs in between block shots and blasts.
- Lobbed shots (mortars, the medieval war bow) arc over walls and come down on the top of the target's column.
- Shield bubbles are hemispheres: shots are stopped where they enter them in 3D.
- Heavy hits (frag grenades, mortar and bombardment shells): a wooden upper floor breaks, a stone or metal one cracks and breaks on the next heavy hit. Plasteel and uranium floors (any metal with at least double hit points) withstand frag grenades and mortar shells; heavier blasts and sky strikes still crack them. A broken floor collapses: buildings on it are destroyed, pawns and items fall, the roof below drops. Cracked floors are taken up and laid again.
- Pawns and items that end up over open air fall to the floor below.
- Locusts (Odyssey) jump up to fights on another floor. Any jump ability can join the same rule.
- Flying animals fly up or down to fights on another floor, away from danger, and back to food, pen or home when stranded.

## Tall mountains

- With **Tall mountains**, mountain areas rise one storey per step of elevation, each level with its own rock top.
- **Mine above** and **Mine below** dig into the level above or below from where your pawns stand, with an X-ray view of that level.
- Build on the rock tops, or carve a home into the mountain over several floors.

## Gravships

- **Ship upper decks** stack on gravship substructure and fly with the ship; ship stairs join the decks.
- Ships with upper decks take off and land in 3D; the landing spot must have room for every deck.

## Compatibility

- Combat Extended is not supported: it loads, but cross-floor combat is turned off.
- Dubs Mint Minimap shows the ground only.
- Patches fail soft: if another mod changes a patched method, only the feature behind that patch turns off, with a log line.
- Packs (both optional): [IsoRim Waylith - VFE Medieval 2 Pack](https://github.com/peren717/IsoRimWaylith-VFEM2Pack), [IsoRim Waylith - Giddy-Up Pack](https://github.com/peren717/IsoRimWaylith-GiddyUpPack).

## For modders

`ProjectZ.Api` (in `IsoRimProjectZ.dll`) holds the public calls, each documented in [`Source/Api.cs`](Source/Api.cs). Check `ProjectZ.Api.Version` for what is there:

- 1: lobbed-shot rules between floors (`RegisterLobbedShot`, `SetLobbed`, `IsLobbed`), `OnDifferentFloors`, `CanSeeBetweenFloors`, `OpenSkyOver`.
- 2:
  - Floors: `HasFloors`, `FloorCount`, `FloorOf` (cell or thing), `GroundCellOf`, `CellOnFloor`, `SameFloor`, `GroundRect`, `StoreyHeight`, `ViewedFloor`, `SetViewedFloor`.
  - Height rule: the XML extension `ProjectZ.FitsUnderFloor` (a tall model that still fits under a floor), `RegisterHeightRule`, `TooTallUnderFloor`.
  - Events: `FloorChanged` (floor built or removed), `PawnChangedFloor`, `AddStepHook` (before a stair, ladder or elevator step; can refuse it).
  - Combat: `Distance3D`, `RangeDistance`, `HeightAccuracyBonus`.
- 3:
  - Stacked cells: `StackedCell` (the cell directly above or below, the rule power conduits use to join floors), `StackedCells`.
  - Vertical links drawn like power risers: `AddRiserSource` / `RemoveRiserSource` with a `RiserSource` made from the addon's own methods: one lists the `RiserLink`s (lower cell, colour, visible ends, overlay line and end mark), one says whether the overlay is shown. Used by the VE Framework Pack for pipes.
- 4:
  - Turrets that turn themselves: `AimPoint` (where to aim, in the shooter's own floor frame, at a target on another floor). When another mod skips `TurretTop.TurretTopTick` for its turret, ProjectZ leaves that gun alone; turn it toward `AimPoint` at your own speed.
  - Gravship cells: `MapToShip` / `ShipToMap` (a map cell to a ship offset and back; an offset keeps its floor, so data stored per ship cell lands on the right deck).
  - `RequestFogRefresh`: runs the floors' fog rule again, after a mod re-fogged the map.
  - `RegisterDeckTerrain`: another foundation that counts as a ship deck. On upper floors it is built like the deck, on the floor support over open air where a deck may go, or in place of a deck when its own rules allow replacing substructure on the ground.
- 5: `ShipCells`: the ship's cells on every floor without the substructure limit (for a mod that leaves some of its own cells out of the count).

Ground-only buildings: add a `GroundOnlyDef` or patch `Defs/Misc/PZ_GroundOnly.xml`. Jump abilities: see `JumpAcross` in the source. Flying between floors: every animal the game lets fly does it; mods that fly their own way add a `FloorFlyersDef` (`fly`, `noFly` race lists) or patch `Defs/Misc/PZ_FloorFlyers.xml`.

## Credits

Path splicing idea from MigCorp - Skiptech (TheMiggiestMig). No code from it is used.

MIT License.
