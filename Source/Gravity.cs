using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Compat G2 (isorim-pz-compat-g2-plan.md, user decisions in isorim-pz-compat-hardening-plan.md "G2 decisions"):
    // gravity at the game's spawn entry points. A cell on an upper band without a floor (open air) or in a void row is
    // "air". Whatever is spawned or placed on air, by the game or any mod going through the game's entry points:
    //   - pawns and items (corpses, minified things, chunks) fall to the first floor below in their column (Column.Landing:
    //     slab, ship deck, mountain top, ground). A void row's thing goes to the edge row of the band below the gap. Pawns
    //     falling one floor or more get the slab-collapse bruises (JumpGrid.Landed).
    //   - a roof passed on the way (the ceiling of a room below open air) breaks like under a drop pod (the game's own
    //     RoofCollapserImmediate.DropRoofInCells for that cell: crush damage under it, rubble, the roof gone), for every
    //     falling thing. A thick (rock) roof never breaks: the fall stops and the thing takes the game's nearest placement
    //     on its own floor (Slab.NearestStandable / the game's "near" search there), as when nothing below holds it.
    //   - buildings (and frames and blueprints of buildings) are refused: the game's refund (GenSpawn.Refund: minified,
    //     else its materials), which then falls like any item. Plants are not spawned.
    //   - filth and loose fire (not burning on a pawn or a building) are not spawned; filth never spreads onto air.
    //   - untouched: motes, projectiles, ethereal things (flyers, skyfallers, explosions...), gas, attached fire, blueprints
    //     and frames of a floor (slabs and decks are built on open air), a gravship being placed (Odyssey), map loading
    //     (load never moves anything: old strays stay), and ProjectZ's own slab collapse (its own fall, Suspend).
    // Taking a slab or deck up (Slab.MoveOff) uses the same rule: what stood on it, and its materials, fall.
    // Hooks: GenSpawn.Spawn (the bottom of every vanilla spawn), GenPlace.TryPlaceThing (the centre of a near/radius search
    // moves down before the search; filth refused), FilthMaker's core (no spreading), Map.FinalizeLoading (the load scope).
    // Without a map with floors in the session every hook leaves on one static flag test (byte-identical: mode gravityn1).
    // Test switch -projectznogravity: no hook acts (the old behaviour, to show the leaks).
    public static class Gravity
    {
        // -projectznogravity (test only)
        public static bool Off;

        [ThreadStatic] private static int suspended;
        private static int loading;

        // counters (tests: mode gravity)
        public static int Fell, FallenFloors, Refused, Refunded, NotSpawned, RoofsBroken, ThickStops, NoLanding;
        public static int ExemptFloorBuild, ExemptGravship, ExemptAttached, OnAirSuspended, OnAirLoading, PawnsBruised;
        // calls into the hooks on maps with floors, and the share that walked a column (P1)
        public static long SpawnCalls, PlaceCalls, FilthCalls, Walks;

        public static void ResetCounters()
        {
            Fell = FallenFloors = Refused = Refunded = NotSpawned = RoofsBroken = ThickStops = NoLanding = 0;
            ExemptFloorBuild = ExemptGravship = ExemptAttached = OnAirSuspended = OnAirLoading = PawnsBruised = 0;
            SpawnCalls = PlaceCalls = FilthCalls = Walks = 0;
        }

        public static string Counters => $"fell {Fell} ({FallenFloors} floors), refused {Refused} (refunded {Refunded}), not spawned {NotSpawned}, roofs broken {RoofsBroken}, thick stops {ThickStops}, no landing {NoLanding}, bruised {PawnsBruised}; exempt: floor builds {ExemptFloorBuild}, gravship {ExemptGravship}, attached {ExemptAttached}; on air while suspended {OnAirSuspended}, loading {OnAirLoading}";

        // ProjectZ's own code that places things on air on purpose (the slab collapse's own fall, test fixtures).
        public static void Suspend() => suspended++;

        public static void Resume() => suspended--;

        public static bool Suspended => suspended > 0;

        public static bool Loading => loading > 0;

        // Gravity applies now (a map with floors seen, not switched off, no scope).
        public static bool On => !Off && suspended == 0 && loading == 0 && !GravshipPlacementUtility.placingGravship;

        // Open air or a void row: an annex cell without a floor.
        public static bool Air(IntVec3 c, Map map) => ProjectZ.IsAnnex(c, map) && c.InBounds(map) && !ProjectZ.HasFloor(c, map);

        public enum Kind
        {
            Untouched,
            Falls,
            Refused,
            NotSpawned
        }

        public static Kind KindOf(Thing t)
        {
            ThingDef def = t.def;
            if (def.IsBlueprint || def.IsFrame)
            {
                return def.entityDefToBuild is TerrainDef ? Kind.Untouched : Kind.Refused;
            }
            switch (def.category)
            {
                case ThingCategory.Pawn:
                case ThingCategory.Item:
                    return Kind.Falls;
                case ThingCategory.Building:
                case ThingCategory.Plant:
                case ThingCategory.PsychicEmitter:
                    return Kind.Refused;
                case ThingCategory.Filth:
                    return Kind.NotSpawned;
                case ThingCategory.Attachment:
                    return t is AttachableThing at && at.parent != null ? Kind.Untouched : Kind.NotSpawned;
                default:
                    return Kind.Untouched;
            }
        }

        // The categories that leave at once (motes on every spawn): never resting on a floor.
        internal static bool Exempt(ThingDef def)
        {
            switch (def.category)
            {
                case ThingCategory.Mote:
                case ThingCategory.Projectile:
                case ThingCategory.Gas:
                    return true;
                case ThingCategory.Ethereal:
                    return !def.IsBlueprint;
                default:
                    return false;
            }
        }

        [ThreadStatic] private static List<IntVec3> broken;

        // The fall of something aimed at air cell c: the landing (roofs passed broken now), or the game's nearest
        // placement on c's own floor when a thick roof stops it or nothing below holds it (standable: the nearest standable
        // cell there, else c; not standable: c's own floor cell, for the game's own near search).
        public static IntVec3 Fall(IntVec3 c, Map map, bool standable, out int fallen)
        {
            if (broken == null)
            {
                broken = new List<IntVec3>();
            }
            Walks++;
            IntVec3 land = Column.Landing(c, map, standable, true, out fallen, out IntVec3 origin, broken);
            if (!land.IsValid)
            {
                fallen = 0;
                bool thick = false;
                IntVec3 g = Levels.Ground(origin, map);
                if (g.IsValid)
                {
                    // (why it stopped: a thick roof somewhere under the start)
                    for (int L = Levels.LevelOf(origin, map); L > Levels.Min(map) && !thick; L--)
                    {
                        IntVec3 below = Levels.Cell(g, L - 1, map);
                        thick = below.IsValid && (map.roofGrid.RoofAt(below)?.isThickRoof ?? false);
                        if (below.IsValid && Column.FloorAt(g, L - 1, map))
                        {
                            break;
                        }
                    }
                }
                if (thick)
                {
                    ThickStops++;
                }
                else
                {
                    NoLanding++;
                }
                if (!standable)
                {
                    return origin.IsValid ? origin : c;
                }
                IntVec3 near = origin.IsValid ? Slab.NearestStandable(origin, map, Levels.LevelOf(origin, map), false) : IntVec3.Invalid;
                return near.IsValid ? near : c;
            }
            if (broken.Count > 0)
            {
                IntVec3[] cells = broken.ToArray();
                broken.Clear();
                foreach (IntVec3 b in cells)
                {
                    if (map.roofGrid.Roofed(b))
                    {
                        RoofsBroken++;
                        ProjectZ.DebugLog($"gravity: the roof at {b} broken by a fall from {c}");
                        RoofCollapserImmediate.DropRoofInCells(b, map);
                    }
                }
            }
            if (land != c)
            {
                Fell++;
                FallenFloors += fallen;
            }
            return land;
        }

        // A spawned pawn on air cell 'from' (its floor just went) falls: moved, its job ended (unless keepJob), bruised from
        // one floor up. False when it stays (no air there, or nowhere to go).
        public static bool FallPawn(Pawn p, IntVec3 from, bool keepJob)
        {
            Map map = p.Map;
            IntVec3 to = Fall(from, map, true, out int fallen);
            if (!to.IsValid || to == from)
            {
                return false;
            }
            p.Position = to;
            p.Notify_Teleported(endCurrentJob: !keepJob || fallen > 0, resetTweenedPos: fallen > 0);
            Bruise(p, fallen);
            return true;
        }

        internal static void Bruise(Pawn p, int fallen)
        {
            if (fallen >= 1 && p.Spawned)
            {
                PawnsBruised++;
                JumpGrid.For(p.Map)?.Landed(p, fallen);
            }
        }

        // A building, plant, filth or loose fire that may not be on air: the game's refund for a building (it falls as
        // any item), destroyed otherwise. Returned (never null: callers use it).
        internal static Thing Refuse(Thing t, IntVec3 loc, Map map, Rot4 rot, Kind kind)
        {
            if (kind == Kind.NotSpawned)
            {
                NotSpawned++;
            }
            else
            {
                Refused++;
            }
            ProjectZ.DebugLog($"gravity: {t} at {loc} {(kind == Kind.NotSpawned ? "not spawned" : "refused")} (no floor there)");
            if (kind == Kind.Refused && t.def.category == ThingCategory.Building && !t.def.IsBlueprint && !t.Destroyed)
            {
                Refunded++;
                t.Position = loc;
                if (t.def.rotatable)
                {
                    t.Rotation = rot;
                }
                GenSpawn.Refund(t, map, CellRect.Empty);
                return t;
            }
            if (!t.Destroyed)
            {
                t.Destroy(DestroyMode.Vanish);
            }
            return t;
        }

        // Any cell of a building's rect on air (a 1x1 thing: its cell).
        internal static bool AnyAir(Thing t, IntVec3 loc, Rot4 rot, Map map)
        {
            IntVec2 size = t.def.Size;
            if (size.x == 1 && size.z == 1)
            {
                return Air(loc, map);
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, size))
            {
                if (Air(c, map))
                {
                    return true;
                }
            }
            return false;
        }

        public static void LoadingEnter() => loading++;

        public static void LoadingExit() => loading = Math.Max(0, loading - 1);
    }

    // The bottom: every spawn in the game ends here (after ShipLocal's net, which runs first).
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
    public static class GenSpawn_Gravity_Patch
    {
        public static bool Prefix(Thing newThing, ref IntVec3 loc, Map map, Rot4 rot, bool respawningAfterLoad, ref Thing __result, out int __state)
        {
            __state = 0;
            if (!BandRadial.AnyAnnex || Gravity.Off || newThing == null || Gravity.Exempt(newThing.def))
            {
                return true;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            Gravity.SpawnCalls++;
            Gravity.Kind kind = Gravity.KindOf(newThing);
            if (kind == Gravity.Kind.Untouched)
            {
                if ((newThing.def.IsBlueprint || newThing.def.IsFrame) && Gravity.Air(loc, map))
                {
                    Gravity.ExemptFloorBuild++;
                }
                else if (newThing.def.category == ThingCategory.Attachment && Gravity.Air(loc, map))
                {
                    Gravity.ExemptAttached++;
                }
                return true;
            }
            bool multi = newThing.def.Size.x > 1 || newThing.def.Size.z > 1;
            if (!multi && loc.z < a.groundZ || !loc.InBounds(map))
            {
                return true;
            }
            bool air = kind == Gravity.Kind.Falls || !multi ? Gravity.Air(loc, map) : Gravity.AnyAir(newThing, loc, rot, map);
            if (!air)
            {
                return true;
            }
            if (respawningAfterLoad || !Gravity.On)
            {
                if (respawningAfterLoad || Gravity.Loading)
                {
                    Gravity.OnAirLoading++;
                }
                else if (GravshipPlacementUtility.placingGravship)
                {
                    Gravity.ExemptGravship++;
                }
                else
                {
                    Gravity.OnAirSuspended++;
                }
                return true;
            }
            if (kind == Gravity.Kind.Falls)
            {
                IntVec3 to = Gravity.Fall(loc, map, true, out int fallen);
                if (to.IsValid && to.InBounds(map))
                {
                    loc = to;
                    __state = fallen;
                }
                return true;
            }
            __result = Gravity.Refuse(newThing, loc, map, rot, kind);
            return false;
        }

        public static void Postfix(Thing __result, int __state)
        {
            if (__state > 0 && __result is Pawn p)
            {
                Gravity.Bruise(p, __state);
            }
        }
    }

    // Place near / radius / direct: a pawn's or item's centre on air moves to its landing BEFORE the game searches near it
    // (else the search would slide it sideways onto the floor next to the edge) and before stacking. Filth (always direct)
    // and loose fire on air: not placed. Buildings and plants go on to the bottom (refund) or the game's own near search
    // (which never picks air: CanSpawnAt asks for a walkable cell).
    [HarmonyPatch(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing), typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class GenPlace_Gravity_Patch
    {
        public static bool Prefix(Thing thing, ref IntVec3 center, Map map, ThingPlaceMode mode, ref Thing lastResultingThing, ref bool __result, out int __state)
        {
            __state = 0;
            if (!BandRadial.AnyAnnex || Gravity.Off || thing == null || Gravity.Exempt(thing.def))
            {
                return true;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || center.z < a.groundZ || !center.InBounds(map))
            {
                return true;
            }
            Gravity.PlaceCalls++;
            if (!Gravity.On || !Gravity.Air(center, map))
            {
                return true;
            }
            Gravity.Kind kind = Gravity.KindOf(thing);
            if (kind == Gravity.Kind.Falls)
            {
                IntVec3 to = Gravity.Fall(center, map, mode == ThingPlaceMode.Direct, out int fallen);
                if (to.IsValid && to.InBounds(map))
                {
                    center = to;
                    __state = fallen;
                }
                return true;
            }
            if (kind == Gravity.Kind.NotSpawned)
            {
                Gravity.NotSpawned++;
                lastResultingThing = null;
                __result = false;
                return false;
            }
            return true;
        }

        public static void Postfix(bool __result, Thing lastResultingThing, int __state)
        {
            if (__state > 0 && __result && lastResultingThing is Pawn p)
            {
                Gravity.Bruise(p, __state);
            }
        }
    }

    // Filth on air: none, and no spreading to a neighbour (at a band edge that could be the slab next to it or, over a
    // one-row gap, another floor).
    [HarmonyPatch(typeof(FilthMaker), "TryMakeFilth", new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef), typeof(IEnumerable<string>), typeof(bool), typeof(Filth), typeof(FilthSourceFlags) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    public static class FilthMaker_Gravity_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref Filth outFilth, ref bool __result)
        {
            if (!BandRadial.AnyAnnex || Gravity.Off)
            {
                return true;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || c.z < a.groundZ || !c.InBounds(map))
            {
                return true;
            }
            Gravity.FilthCalls++;
            if (!Gravity.On || !Gravity.Air(c, map))
            {
                return true;
            }
            Gravity.NotSpawned++;
            outFilth = null;
            __result = false;
            return false;
        }
    }

    // Load never moves anything (things already on air in a save stay there).
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeLoading))]
    public static class FinalizeLoading_Gravity_Patch
    {
        public static void Prefix() => Gravity.LoadingEnter();

        public static void Finalizer() => Gravity.LoadingExit();
    }
}
