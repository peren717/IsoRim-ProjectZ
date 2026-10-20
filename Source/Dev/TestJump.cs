using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode jump (jumps between floors, JumpFlights; run with -projectzfloors=2). A cleared field with three floor-2 slabs:
    // P (9x9 over a roofed ground area, a roofed corner on floor 2), Q (5x9, a 4-cell open gap east of P) and R (9x9,
    // far out, for the range edge). A drafted colonist wearing a jump pack:
    //   - targeting (Verb.ValidateTarget, what the targeter checks): a roofed floor-2 cell refused (accepted once its roof
    //     is lifted), a cell within the flat range but out of the 3D range refused (one just inside accepted), a cell on
    //     the slab straight over a pawn standing under it refused (no line through the slab), a void row refused;
    //   - the flight's arc (JumpArc): a far jump lands 3 cells past the rim where a short steep one is refused; with
    //     -projectzfloors=3 a jump up under a floor-3 slab overhang is refused (the straight line would pass under it);
    //   - real jumps ordered as the targeter does (Verb.OrderForceTarget) on real ticks: ground -> floor 2, floor 2 ->
    //     ground, floor 2 -> floor 2 across the gap; each lands where asked, unhurt; flights between floors filmed in
    //     bursts at yaw 45 and 225 with floor 2 viewed and hidden;
    //   - no pack and no stairs: ordered down off the slab edge, the pawn stays up (no voluntary jumps since 2026-10-19);
    //   - a sanguophage's longjump ground -> floor 2;
    //   - the game's own AI jump: a colony paramedic mech given a rescue of a downed colonist on floor 2 jumps up to it
    //     (JobGiver_AIJumpToJobTarget, its constant think tree).
    public static partial class TestRunner
    {
        private sealed class JumpCase
        {
            public string name;
            public IntVec3 from, to;   // map cells
            public int view = -1;      // -1: no shots; 0 ground (floor 2 hidden), 1 floor 2
            public float yaw;          // IsoRim freeYaw (0 = 45, 180 = 225)
            public string who = "pack";
        }

        private static readonly float[] jpFracs = { 0.2f, 0.45f, 0.7f, 0.9f };

        private static readonly AccessTools.FieldRef<PawnFlyer, int> jpTicksFlying = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> jpTicksTotal = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");

        private static IntVec3 jpO;
        private static CellRect jpP, jpQ, jpR;
        private static List<JumpCase> jpCases;
        private static int jpCase, jpShot, jpInjBefore, jpFlightTicks;
        private static Pawn jpJumper, jpPlain, jpSang, jpMech, jpDowned;
        private static Verb jpVerb;
        private static PawnFlyer jpFlyer;
        private static float jpOldYaw, jpOldMin;
        private static bool jpMechFlew;
        private static string jpMechFlight;

        private static IntVec3 JO(int x, int z) => new IntVec3(jpO.x + x, 0, jpO.z + z);

        private static IntVec3 JUp(int x, int z, Map map) => ProjectZ.CellOnFloor(JO(x, z), 1, map);

        private static Pawn JumperOf(JumpCase c) => c.who == "sang" ? jpSang : jpJumper;

        private static Verb VerbOf(JumpCase c) => c.who == "sang" ? jpSang.abilities.GetAbility(DefDatabase<AbilityDef>.GetNamed("Longjump")).verb : jpVerb;

        private static bool StepJump(Map map)
        {
            switch (stage)
            {
                case "jp_setup":
                    SetupJump(map);
                    return true;
                case "jp_targets":
                    JumpTargets(map);
                    jpCase = 0;
                    Wait(5, "jp_case");
                    return true;
                case "jp_case":
                {
                    if (jpCase >= jpCases.Count)
                    {
                        Wait(2, "jp_plain");
                        return true;
                    }
                    JumpCase c = jpCases[jpCase];
                    Pawn p = JumperOf(c);
                    Heal(p);
                    Teleport(p, c.from);
                    Draft(p, true);
                    Recharge(map);
                    jpInjBefore = Injuries(p);
                    Verb v = VerbOf(c);
                    bool ok = v.ValidateTarget(c.to, false);
                    Result($"{c.name}: the target is accepted", ok, $"{c.from} (floor {ProjectZ.FloorOf(c.from, map) + 1}) -> {c.to} (floor {ProjectZ.FloorOf(c.to, map) + 1}), 3D {Range3D.Distance(c.from, c.to, map):0.00} of range {v.EffectiveRange:0.0}");
                    if (!ok)
                    {
                        jpCase++;
                        Wait(2, "jp_case");
                        return true;
                    }
                    if (c.view >= 0)
                    {
                        IsoRimBridge.SetOption("freeYaw", c.yaw);
                    }
                    v.OrderForceTarget(c.to);
                    jpFlyer = null;
                    TickUntil(300, () => p.ParentHolder is PawnFlyer, "jp_fly", 1);
                    return true;
                }
                case "jp_fly":
                {
                    tickWatch = null;
                    JumpCase c = jpCases[jpCase];
                    Pawn p = JumperOf(c);
                    jpFlyer = p.ParentHolder as PawnFlyer;
                    if (jpFlyer == null)
                    {
                        Result($"{c.name}: takes off", false, $"no flight; job {p.CurJob?.def?.defName}, stance {p.stances?.curStance}");
                        jpCase++;
                        Wait(2, "jp_case");
                        return true;
                    }
                    jpFlightTicks = jpTicksTotal(jpFlyer);
                    Info($"{c.name}: flying, {jpFlightTicks} ticks (3D {Range3D.Distance(c.from, c.to, map):0.0} cells at {jpFlyer.def.pawnFlyer.flightSpeed} cells/s, row distance {c.from.DistanceTo(c.to):0.0})");
                    jpShot = 0;
                    if (c.view >= 0)
                    {
                        JumpShotWait();
                    }
                    else
                    {
                        TickUntil(600, () => p.Spawned, "jp_land", 1);
                    }
                    return true;
                }
                case "jp_shoot":
                {
                    tickWatch = null;
                    JumpCase c = jpCases[jpCase];
                    IntVec3 ga = ProjectZ.GroundCellOf(c.from, map), gb = ProjectZ.GroundCellOf(c.to, map);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    ShotAt(map, c.view, new IntVec3((ga.x + gb.x) / 2, 0, (ga.z + gb.z) / 2), 8f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    string pose = "landed";
                    if (jpFlyer != null && jpFlyer.Spawned)
                    {
                        pose = $"tick {jpTicksFlying(jpFlyer)}/{jpTicksTotal(jpFlyer)} at {jpFlyer.Position} (floor {ProjectZ.FloorOf(jpFlyer.Position, map) + 1})";
                        if (JumpFlights.PoseOf(jpFlyer, out JumpFlights.Pose ps))
                        {
                            pose += $", band {ps.band + 1}, rise {ps.rise:0.00}, height {ps.height:0.00}, shadow on floor {ps.shadow + 1}, over {ps.ground}";
                        }
                    }
                    Info($"{c.name} shot {jpShot}: {pose}");
                    Wait(25, "jp_take");
                    return true;
                }
                case "jp_take":
                {
                    JumpCase c = jpCases[jpCase];
                    bool landed = jpShot >= jpFracs.Length;
                    ZCapture($"jump_{c.name}_{(landed ? "landed" : "s" + jpShot)}");
                    jpShot++;
                    if (landed)
                    {
                        Wait(3, "jp_landed");
                    }
                    else if (jpShot >= jpFracs.Length)
                    {
                        Wait(3, "jp_tickland");
                    }
                    else
                    {
                        Wait(3, "jp_shotwait");
                    }
                    return true;
                }
                case "jp_shotwait":
                    JumpShotWait();
                    return true;
                case "jp_tickland":
                {
                    Pawn p = JumperOf(jpCases[jpCase]);
                    TickUntil(600, () => p.Spawned, "jp_landshot", 1);
                    return true;
                }
                case "jp_landshot":
                    tickWatch = null;
                    StartTicks(20, "jp_shoot");
                    return true;
                case "jp_land":
                    tickWatch = null;
                    StartTicks(20, "jp_landed");
                    return true;
                case "jp_landed":
                {
                    JumpCase c = jpCases[jpCase];
                    Pawn p = JumperOf(c);
                    bool there = p.Spawned && ProjectZ.FloorOf(p.Position, map) == ProjectZ.FloorOf(c.to, map) && p.Position.DistanceTo(c.to) <= 1.5f;
                    Result($"{c.name}: lands where asked", there, $"at {p.Position} (floor {ProjectZ.FloorOf(p.Position, map) + 1}), asked {c.to}");
                    int inj = Injuries(p) - jpInjBefore;
                    Result($"{c.name}: unhurt by the landing", inj == 0, $"new injuries {inj} ({InjuryNames(p)})");
                    if (JumpRules.Cross(c.from, c.to, map))
                    {
                        float want = Mathf.Max(Range3D.Distance(c.from, c.to, map), 1f) / 12f * 60f;
                        Result($"{c.name}: flight time from its 3D length", Mathf.Abs(jpFlightTicks - want) <= 3f || jpFlightTicks <= 30, $"{jpFlightTicks} ticks, expected ~{want:0}");
                    }
                    jpCase++;
                    Wait(2, "jp_case");
                    return true;
                }
                case "jp_plain":
                {
                    // ordered down off P's south edge with no pack (and no stairs): it stays up (no voluntary jumps since 2026-10-19)
                    IntVec3 from = JUp(5, 10, map), to = JO(5, 5);
                    Heal(jpPlain);
                    Teleport(jpPlain, from);
                    Draft(jpPlain, true);
                    jpInjBefore = Injuries(jpPlain);
                    jpPlain.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, to), JobTag.Misc);
                    Info($"plain jump-down: {jpPlain} from {from} (floor 2, no pack) to {to}");
                    TickUntil(600, () => jpPlain.Spawned && ProjectZ.FloorOf(jpPlain.Position, map) == 0, "jp_plain2", 1);
                    return true;
                }
                case "jp_plain2":
                {
                    tickWatch = null;
                    int inj = Injuries(jpPlain) - jpInjBefore;
                    Result("no pack, no stairs: ordered down off the edge the pawn stays up (no voluntary jump), unhurt", ProjectZ.FloorOf(jpPlain.Position, map) == 1 && inj == 0, $"at {jpPlain.Position}, new injuries {inj} ({InjuryNames(jpPlain)})");
                    Wait(2, "jp_mech");
                    return true;
                }
                case "jp_mech":
                    SetupMech(map);
                    return true;
                case "jp_mech2":
                {
                    tickWatch = null;
                    bool up = jpMech.Spawned && ProjectZ.FloorOf(jpMech.Position, map) == 1;
                    Result("paramedic mech: the game's AI rescue jump reaches the downed colonist on floor 2", jpMechFlew && up,
                        $"flew {jpMechFlew} ({jpMechFlight}), now at {(jpMech.Spawned ? jpMech.Position.ToString() : "in flight")} floor {(jpMech.Spawned ? ProjectZ.FloorOf(jpMech.Position, map) + 1 : -1)}, job {jpMech.CurJob?.def?.defName}");
                    Result("paramedic mech: unhurt by the landing", Injuries(jpMech) == 0, InjuryNames(jpMech));
                    StartTicks(600, "jp_mech3");
                    return true;
                }
                case "jp_mech3":
                    Info($"paramedic 600 ticks later: at {(jpMech.Spawned ? jpMech.Position.ToString() : "in flight")} floor {(jpMech.Spawned ? ProjectZ.FloorOf(jpMech.Position, map) + 1 : -1)}, job {jpMech.CurJob?.def?.defName}, carrying {jpMech.carryTracker?.CarriedThing}; downed colonist at {(jpDowned.Spawned ? jpDowned.Position.ToString() : jpDowned.ParentHolder?.ToString())}");
                    IsoRimBridge.SetOption("freeYaw", jpOldYaw);
                    Find.CameraDriver.config.sizeRange.min = jpOldMin;
                    Finish();
                    return true;
            }
            return false;
        }

        private static void JumpShotWait()
        {
            PawnFlyer f = jpFlyer;
            float frac = jpFracs[jpShot];
            TickUntil(400, () => f == null || !f.Spawned || jpTicksFlying(f) >= frac * jpTicksTotal(f), "jp_shoot", 1);
        }

        private static void Recharge(Map map)
        {
            foreach (Apparel a in jpJumper.apparel.WornApparel)
            {
                CompApparelVerbOwner_Charged c = a.GetComp<CompApparelVerbOwner_Charged>();
                if (c != null)
                {
                    Traverse.Create(c).Field("remainingCharges").SetValue(c.MaxCharges);
                }
            }
            if (jpSang?.genes != null)
            {
                Gene_Hemogen h = jpSang.genes.GetFirstGeneOfType<Gene_Hemogen>();
                if (h != null)
                {
                    h.Value = h.Max;
                }
                foreach (Ability ab in jpSang.abilities.AllAbilitiesForReading)
                {
                    ab.ResetCooldown();
                }
            }
        }

        private static void JumpTargets(Map map)
        {
            Pawn p = jpJumper;
            IntVec3 g = JO(8, 3);
            Teleport(p, g);
            Draft(p, true);
            float range = jpVerb.EffectiveRange;
            Info($"jumper {p} with {string.Join(",", p.apparel.WornApparel.Select(a => a.def.defName))}, jump range {range:0.00}, at {g}");
            // roofed landing: refused, accepted once the roof is lifted
            IntVec3 roofed = JUp(10, 16, map);   // the roofed corner's outer cell (the arc into its inner cells passes under its roof)
            bool withRoof = jpVerb.ValidateTarget(roofed, false);
            RoofDef r = map.roofGrid.RoofAt(roofed);
            map.roofGrid.SetRoof(roofed, null);
            bool without = jpVerb.ValidateTarget(roofed, false);
            map.roofGrid.SetRoof(roofed, r);
            Result("refused: a roofed landing on floor 2 (accepted without the roof)", !withRoof && without, $"{roofed} roof {r?.defName}: with {withRoof}, without {without}");
            // range edge: flat in range, 3D out; and one just inside
            IntVec3 outC = IntVec3.Invalid, inC = IntVec3.Invalid;
            float best = 0f, bestIn = 0f;
            foreach (IntVec3 c in jpR)
            {
                IntVec3 u = ProjectZ.CellOnFloor(c, 1, map);
                float flat = (c - g).LengthHorizontal;
                float d3 = Range3D.Distance(g, u, map);
                if (!JumpRules.Sight(g, u, map) || !Column.OpenAbove(u, map))
                {
                    continue;
                }
                if (flat <= range && d3 > range && d3 - range > best)
                {
                    best = d3 - range;
                    outC = u;
                }
                if (d3 <= range && d3 > bestIn)
                {
                    bestIn = d3;
                    inC = u;
                }
            }
            if (outC.IsValid)
            {
                Result("refused: within the flat range but out of the 3D range", !jpVerb.ValidateTarget(outC, false), $"{outC}: flat {(ProjectZ.GroundCellOf(outC, map) - g).LengthHorizontal:0.00}, 3D {Range3D.Distance(g, outC, map):0.00}, range {range:0.00}");
            }
            else
            {
                Info("no cell of R between the flat and the 3D range (range " + range + ")");
            }
            Result("accepted: a floor-2 cell just inside the 3D range", inC.IsValid && jpVerb.ValidateTarget(inC, false), $"{inC}: 3D {bestIn:0.00}");
            // under the slab: no line up through it
            IntVec3 under = JO(8, 12);
            Teleport(p, under);
            IntVec3 above = JUp(8, 16, map);
            Result("refused: from under the slab to a cell on it (no line through the slab)", !jpVerb.ValidateTarget(above, false), $"{under} (roof {map.roofGrid.RoofAt(under)?.defName}) -> {above}, 3D {Range3D.Distance(under, above, map):0.00}");
            JumpArc(map, p);
            // a void row
            AnnexMapComponent an = ProjectZ.Annex(map);
            IntVec3 v = new IntVec3(g.x, 0, an.groundZ + (an.stride - an.groundZ) / 2);
            Teleport(p, g);
            Result("refused: a void row between floors", !jpVerb.ValidateTarget(v, false), $"{v} (floor {ProjectZ.FloorOf(v, map)})");
            // for comparison, the same cell as an up-jump on the ground's own rules would be: the row distance
            Info($"a floor-2 target {JUp(8, 11, map)} is {g.DistanceTo(JUp(8, 11, map)):0.0} rows away (the game's own measure), {Range3D.Distance(g, JUp(8, 11, map), map):0.0} in 3D");
        }

        // The flight's arc (user decision 2026-10-16): how far past P's south rim (ground row 10) a jump up may land follows
        // from the jump's length; a high arc under a floor-3 slab overhang is stopped (with 3 floors).
        private static void JumpArc(Map map, Pawn p)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            PawnFlyerProperties props = JumpRules.Props(JumpRules.FlyerOf(jpVerb));
            Info($"arc: storey {IsoRimBridge.StoreyHeight:0.00}, body {JumpRules.Body:0.00}, flyer {JumpRules.FlyerOf(jpVerb).defName} heightFactor {props.heightFactor}");
            bool Can(IntVec3 from, IntVec3 to)
            {
                Teleport(p, from);
                return jpVerb.ValidateTarget(to, false);
            }
            bool Straight(IntVec3 from, IntVec3 to)
            {
                return Heights.Clear(ProjectZ.GroundCellOf(from, map), ProjectZ.GroundCellOf(to, map), ProjectZ.FloorOf(from, map) * Heights.Storey + Heights.Eye,
                    ProjectZ.FloorOf(to, map) * Heights.Storey + Heights.Middle, map);
            }
            int Deepest(IntVec3 from)
            {
                int d = 0;
                for (int n = 1; n <= 9; n++)
                {
                    if (Can(from, JUp(8, 9 + n, map)))
                    {
                        d = n;
                    }
                }
                return d;
            }
            IntVec3 near = JO(8, 9), mid = JO(8, 5), far = JO(8, 1);
            int dn = Deepest(near), dm = Deepest(mid), df = Deepest(far);
            Info($"arc: deepest landing past the rim from 1 cell out {dn}, from 5 out {dm}, from 9 out {df} (cells)");
            IntVec3 deep = JUp(8, 12, map);
            bool farOk = Can(far, deep);
            bool nearOk = Can(near, deep);
            bool nearEdge = Can(near, JUp(8, 10, map));
            Result("arc: a far jump (11 cells) lands 3 cells past the slab's rim", farOk, $"{far} -> {deep}");
            Result("arc: a short steep jump (3 cells) to the same depth is refused, onto the rim's first cell accepted", !nearOk && nearEdge,
                $"{near} -> {deep}: {nearOk} (the old straight line: {Straight(near, deep)}); -> {JUp(8, 10, map)}: {nearEdge}");
            Result("arc: the far jump lands deeper than the short one", df > dn, $"deepest from 9 out {df}, from 1 out {dn}");
            Teleport(p, JO(8, 3));
            if (an.floors < 3)
            {
                Info("arc: the slab-overhang case needs 3 floors (-projectzfloors=3)");
                return;
            }
            // a floor-3 slab overhang (rows 6..8 over Q's columns, roofed on floor 2 under it) over the arc's top
            IntVec3 from = JO(19, 3), to = JUp(19, 11, map);
            CellRect over = new CellRect(jpO.x + 17, jpO.z + 6, 5, 3);
            bool before = Can(from, to);
            foreach (IntVec3 g in over)
            {
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), RoofDefOf.RoofConstructed);
                IntVec3 u = ProjectZ.CellOnFloor(g, 2, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
            bool with = Can(from, to);
            bool straight = Straight(from, to);
            foreach (IntVec3 g in over)
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 2, map), PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), null);
            }
            bool after = Can(from, to);
            Result("arc: a jump up under a floor-3 slab overhang is refused (its arc hits the slab; the straight line passes under it)", before && !with && after && straight,
                $"{from} -> {to}, overhang over rows {over.minZ}..{over.maxZ}: before {before}, with {with}, removed {after}; straight line {straight}");
            Teleport(p, JO(8, 3));
        }

        private static void SetupJump(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map);
            while (cols.Count < 3)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
                GenSpawn.Spawn(extra, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
                cols = Colonists(map);
            }
            if (an == null || an.floors < 2)
            {
                Result("jump setup", false, $"needs 2 floors (-projectzfloors=2); floors {an?.floors}");
                Finish();
                return;
            }
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 40 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 40 < map.Size.x - 3; x += 4)
                {
                    CellRect rr = new CellRect(x, z, 40, 40);
                    if (rr.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = rr.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = rr;
                    }
                }
            }
            if (found.Area == 0)
            {
                Result("jump field", false, "map too small");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            DebugSettings.enableRandomMentalStates = false;
            jpO = new IntVec3(found.minX, 0, found.minZ + 6);
            jpP = new CellRect(jpO.x + 4, jpO.z + 10, 9, 9);
            jpQ = new CellRect(jpO.x + 17, jpO.z + 10, 5, 9);
            jpR = new CellRect(jpO.x + 24, jpO.z + 12, 9, 9);
            foreach (CellRect rect in new[] { jpP, jpQ, jpR })
            {
                foreach (IntVec3 g in rect)
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                    map.fogGrid.Unfog(u);
                }
            }
            // a roofed corner of P on floor 2
            foreach (IntVec3 g in new CellRect(jpO.x + 10, jpO.z + 16, 3, 3))
            {
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), RoofDefOf.RoofConstructed);
            }
            // everyone else out of the field
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, true);
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4));
            }
            cols = Colonists(map);
            jpJumper = cols[0];
            jpPlain = cols[1];
            jpDowned = cols[2];
            Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
            jpJumper.apparel.Wear(pack, dropReplacedApparel: false);
            jpVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
            // a sanguophage colonist (longjump legs)
            jpSang = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, forcedXenotype: XenotypeDefOf.Sanguophage));
            GenSpawn.Spawn(jpSang, JO(2, 2), map);
            Info($"field {found}, origin {jpO}; P {jpP}, Q {jpQ}, R {jpR} (floor-2 slabs over roofed ground); storey {IsoRimBridge.StoreyHeight:0.00}; sanguophage {jpSang} longjump {jpSang.abilities?.GetAbility(DefDatabase<AbilityDef>.GetNamed("Longjump")) != null}");
            jpCases = new List<JumpCase>
            {
                new JumpCase { name = "up_view2_yaw45", from = JO(8, 3), to = JUp(8, 11, map), view = 1, yaw = 0f },
                new JumpCase { name = "up_view2_yaw225", from = JO(8, 3), to = JUp(8, 11, map), view = 1, yaw = 180f },
                new JumpCase { name = "up_view1_yaw45", from = JO(8, 3), to = JUp(8, 11, map), view = 0, yaw = 0f },
                new JumpCase { name = "up_view1_yaw225", from = JO(8, 3), to = JUp(8, 11, map), view = 0, yaw = 180f },
                new JumpCase { name = "down_view2_yaw45", from = JUp(8, 10, map), to = JO(8, 3), view = 1, yaw = 0f },
                new JumpCase { name = "down_view1_yaw225", from = JUp(8, 10, map), to = JO(8, 3), view = 0, yaw = 180f },
                new JumpCase { name = "down_deep", from = JUp(8, 13, map), to = JO(8, 1) },
                new JumpCase { name = "up_far_deep", from = JO(8, 1), to = JUp(8, 14, map), view = 1, yaw = 0f },
                new JumpCase { name = "across_gap", from = JUp(12, 14, map), to = JUp(17, 14, map), view = 1, yaw = 0f },
                new JumpCase { name = "up_far", from = JO(8, 3), to = JUp(28, 15, map) },
                new JumpCase { name = "sang_up", from = JO(10, 3), to = JUp(10, 11, map), who = "sang" },
            };
            jpOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            jpOldMin = Find.CameraDriver.config.sizeRange.min;
            Wait(10, "jp_targets");
        }

        private static void SetupMech(Map map)
        {
            // stairs inside P (ramp on the ground, landing on floor 2): the rescue has a walking route, so its job runs
            IntVec3 st = JO(5, 16);
            foreach (IntVec3 c in StairShape.Footprint(st, Rot4.North))
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(c, 1, map), PZDefOf.PZ_OpenAir);
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, st, map, Rot4.North);
            Thing bed = Place(ThingDefOf.Bed, ThingDefOf.WoodLog, JO(1, 1), map, Rot4.North);
            Teleport(jpDowned, JUp(6, 11, map));
            Draft(jpDowned, false);
            Hediff an = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, jpDowned);
            an.Severity = 1f;
            jpDowned.health.AddHediff(an);
            jpMech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mech_Paramedic"), Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(jpMech, JO(6, 2), map);
            Ability jump = jpMech.abilities?.GetAbility(DefDatabase<AbilityDef>.GetNamed("LongjumpMechLauncher"));
            Info($"paramedic {jpMech} at {jpMech.Position}, launcher {(jump == null ? "missing" : "range " + jump.verb.EffectiveRange + ", min " + jump.verb.verbProps.minRange + ", can cast " + jump.CanCast.Accepted)}; downed {jpDowned} {jpDowned.Downed} at {jpDowned.Position}, 3D {Range3D.Distance(jpMech.Position, jpDowned.Position, map):0.00}; bed {bed}");
            Job job = JobMaker.MakeJob(JobDefOf.Rescue, jpDowned, bed);
            job.count = 1;
            jpMech.jobs.StartJob(job, JobCondition.InterruptForced);
            Info($"paramedic job {jpMech.CurJob?.def?.defName}");
            jpMechFlew = false;
            jpMechFlight = "";
            TickUntil(900, () =>
            {
                if (!jpMechFlew && jpMech.ParentHolder is PawnFlyer f)
                {
                    jpMechFlew = true;
                    jpMechFlight = $"{jpTicksTotal(f)} ticks to {Traverse.Create(f).Field("destCell").GetValue<IntVec3>()}";
                }
                return jpMechFlew && jpMech.Spawned;
            }, "jp_mech2", 1);
        }
    }
}
