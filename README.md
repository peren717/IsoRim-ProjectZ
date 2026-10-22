# IsoRim ProjectZ

Work-in-progress addon for [IsoRim Waylith](https://github.com/peren717/IsoRim-1.7) (RimWorld 1.7): stacked floors on one map.

- Upper floors are built over roofed rooms; an upper floor is the roof of the room below, and heat passes between floors.
- Ways between floors:
  - Stairs: 1x4 (a 3-cell ramp and a solid end), with an open stairwell. Ship stairs for gravships.
  - Elevator: powered, 1x1, stops at every floor above; climbed like a ladder without power.
  - Improvised ladders: free, built in a second from an upper floor's edge, gone after a day.
  - Siege ladders and lifts: raiders (lifts: mechanoids) put them up to reach upper floors; they fall apart a day after the raid.
- Pawns path between floors over all of these.
- Shooting between floors from the edge of a floor, with an accuracy bonus when shooting down.
- 3D view locked on and wall height fixed while it is active.

Not for normal play yet, and not safe to remove from a save that has floors.

Requires Harmony and IsoRim Waylith.

For modders: `ProjectZ.Api` (in IsoRimProjectZ.dll) has the public calls, each documented in `Source/Api.cs`. Check `ProjectZ.Api.Version` for what is there (1: lobbed shots between floors, floor and line-of-sight helpers).

Path splicing idea from MigCorp - Skiptech (TheMiggiestMig). No code from it is used.

MIT License.
