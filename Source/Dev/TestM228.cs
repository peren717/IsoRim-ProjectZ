using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode m228 (wires between floors, CrossFloorWires; -projectzfloors=3 -projectzseed=7). Real game flow with seeded ticks,
    // two isolated fixtures on cleared concrete fields:
    //   W (power, origin mO): ground conduit line (dx 0..6, dz 0); floor-2 slab over dx -1..19, dz 1..11; floor-2 lamps
    //     dx 1,3,5 dz 4 (no floor-2 transmitter in range: they hook down to the line); floor-2 batteries dx 13,15,17 dz 8;
    //     ground lamps dx 14,16 dz 5 (hook up to the batteries); floor-2 lamp dx 15 dz 4 (own floor, the control); a ground
    //     lamp at dx 18 dz 4 placed with the build designator and built by a colonist (hooks up).
    //   D (deathrest, origin mD): deathrest casket on the ground, a colonist with the Deathrest gene deathresting in it (real
    //     job); a hemopump spawned on floor 2 while it deathrests (PostSpawnSetup tries the binding), powered by a charged
    //     battery on floor 2.
    // Checks: hookups; no printed wire longer than a hookup (probe postfix, only prints that ran); per shot the segments
    // CrossFloorWires drew (kinds, counts, ends on their floors, cut at the slab when the other floor is hidden).
    public static partial class TestRunner
    {
        private static IntVec3 mO = new IntVec3(20, 0, 14);
        private static IntVec3 mD = new IntVec3(70, 0, 14);
        private static int mShot;
        private static bool mProbe;
        private static bool mForcePump;
        private static int mOffErrors;

        public static void MCanUse_Postfix(Thing building, ref bool __result)
        {
            if (mForcePump && building == mPump && mPump != null)
            {
                __result = true;
            }
        }
        private static int mLongPrints, mLongGhosts;
        private static IntVec3 mBuiltCell;
        private static Pawn mRester;
        private static Thing mPump;
        private static readonly List<CrossFloorWires.Segment> mSegs = new List<CrossFloorWires.Segment>();

        private const int MCross = 6;   // cross-floor hookups in W: 3 down, 2 up, the built one up

        private enum MAim { None, Lamp, Conduit }

        private struct MShotDef
        {
            public string n;
            public int view;
            public IntVec3 g;     // camera centre (ground cell)
            public float size;
            public MAim aim;
            public int gx, gz, gk;   // ghost cell (W offsets) and its floor
        }

        private static MShotDef MS(string n, int view, MAim aim, int gx = 0, int gz = 0, int gk = 0, bool deathrest = false, float size = 16f)
        {
            IntVec3 c = deathrest ? new IntVec3(mD.x + 3, 0, mD.z + 3) : new IntVec3(mO.x + 8, 0, mO.z + 3);
            return new MShotDef { n = n, view = view, g = c, size = size, aim = aim, gx = gx, gz = gz, gk = gk };
        }

        private static MShotDef[] mShots;

        private static ThingDef MLamp => DefDatabase<ThingDef>.GetNamed("StandingLamp");

        private static IntVec3 MAt(int dx, int dz, int k, Map map) => Levels.Cell(new IntVec3(mO.x + dx, 0, mO.z + dz), k, map);

        private static IntVec3 MDAt(int dx, int dz, int k, Map map) => Levels.Cell(new IntVec3(mD.x + dx, 0, mD.z + dz), k, map);

        private static int MFloor(IntVec3 c, Map map) => c.IsValid ? ProjectZ.FloorOf(c, map) + 1 : 0;

        private static void MProbePatch()
        {
            if (mProbe)
            {
                return;
            }
            mProbe = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.m228");
            h.Patch(AccessTools.Method(typeof(PowerNetGraphics), nameof(PowerNetGraphics.RenderAnticipatedWirePieceConnecting)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(MAnticipated_Postfix)));
            h.Patch(AccessTools.Method(typeof(PowerNetGraphics), nameof(PowerNetGraphics.PrintWirePieceConnecting)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(MPrinted_Postfix)));
            h.Patch(AccessTools.Method(typeof(Gene_Deathrest), "CanUseBindableNow"), postfix: new HarmonyMethod(typeof(TestRunner), nameof(MCanUse_Postfix)));
        }

        private static readonly HashSet<string> mSeen = new HashSet<string>();

        private static void MLogOnce(string s)
        {
            if (mSeen.Add(s))
            {
                Info(s);
            }
        }

        // (postfixes: they see whether the game's own draw ran)
        public static void MAnticipated_Postfix(IntVec3 userPos, Rot4 rotation, IntVec2 thingSize, Thing transmitter, bool __runOriginal)
        {
            float len = (transmitter.TrueCenter() - GenThing.TrueCenter(userPos, rotation, thingSize, 0f)).MagnitudeHorizontal();
            if (__runOriginal && len > 9f)
            {
                mLongGhosts++;
            }
            MLogOnce($"PROBE ghost wire {userPos} f{MFloor(userPos, transmitter.Map)} -> {transmitter.def.defName} {transmitter.Position} f{MFloor(transmitter.Position, transmitter.Map)}, length {len:F1}, game's draw {(__runOriginal ? "ran" : "skipped")}");
        }

        public static void MPrinted_Postfix(SectionLayer layer, Thing A, Thing B, bool forPowerOverlay, bool __runOriginal)
        {
            float len = (B.TrueCenter() - A.TrueCenter()).MagnitudeHorizontal();
            if (__runOriginal && len > 9f)
            {
                mLongPrints++;
            }
            MLogOnce($"PROBE printed wire ({(forPowerOverlay ? "overlay" : "things")}) {A.def.defName} {A.Position} f{MFloor(A.Position, A.Map)} -> {B.def.defName} {B.Position} f{MFloor(B.Position, B.Map)}, length {len:F1}, game's print {(__runOriginal ? "ran" : "skipped")}");
        }

        private static string MSegText(IEnumerable<CrossFloorWires.Segment> segs) =>
            string.Join("; ", segs.Select(s => $"{s.kind} f{s.floorA + 1}->f{s.floorB + 1} ({s.a.x:F1},{s.a.y:F2},{s.a.z:F1})->({s.b.x:F1},{s.b.y:F2},{s.b.z:F1}){(s.cut ? " cut" : "")}"));

        private static float MSlabY(int floor, float alt) => floor * IsoRimBridge.StoreyHeight / IsoRimBridge.Squash + alt;

        // a cut end: under the hidden floor's slab (IsoRim Floors.SlabThickness 0.12)
        private static float MCutY(int floor, float alt) => (floor * IsoRimBridge.StoreyHeight - 0.12f) / IsoRimBridge.Squash + alt;

        private static bool StepM228(Map map)
        {
            switch (stage)
            {
                case "m228_setup":
                    MSetup(map);
                    return true;

                // ---- the real build: a colonist builds a ground lamp placed with the build designator
                case "m228_build":
                {
                    Designator d = BuildCopyCommandUtility.FindAllowedDesignator(MLamp) ?? new Designator_Build(MLamp);
                    FloorView.Set(0);
                    Find.DesignatorManager.Select(d);
                    IntVec3 g = new IntVec3(mO.x + 18, 0, mO.z + 4);
                    AcceptanceReport r = UiDesignate(ScreenOf(g, 0), ScreenOf(g, 0));
                    Find.DesignatorManager.Deselect();
                    mBuiltCell = g;
                    Info($"lamp designated at {g}: {r.Accepted} '{r.Reason}', blueprint {g.GetThingList(map).Any(t => t is Blueprint)}");
                    if (!g.GetThingList(map).Any(t => t is Blueprint))
                    {
                        GenConstruct.PlaceBlueprintForBuild(MLamp, g, map, Rot4.North, Faction.OfPlayer, null);
                        Info("  (the designator missed the cell: blueprint placed directly)");
                    }
                    SetWork(map, true);
                    SeedTicks("m228_build");
                    TickUntil(30000, () => g.GetThingList(map).Any(t => t.def == MLamp), "m228_built", 20);
                    return true;
                }
                case "m228_built":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("a colonist builds the ground lamp", mBuiltCell.GetThingList(map).Any(t => t.def == MLamp), $"after {ticksTotal} ticks");
                    SeedTicks("m228_settle");
                    StartTicks(120, "m228_check");
                    return true;

                case "m228_check":
                {
                    (int dx, int dz, int k, int tdx, int tdz, int tk)[] want =
                    {
                        (1, 4, 1, 1, 0, 0), (3, 4, 1, 3, 0, 0), (5, 4, 1, 5, 0, 0), (14, 5, 0, 13, 8, 1), (16, 5, 0, 15, 8, 1), (15, 4, 1, 13, 8, 1), (18, 4, 0, 17, 8, 1),
                    };
                    foreach (var w in want)
                    {
                        IntVec3 lc = MAt(w.dx, w.dz, w.k, map);
                        CompPower pc = lc.GetThingList(map).FirstOrDefault(t => t.def == MLamp)?.TryGetComp<CompPower>();
                        IntVec3 p = pc?.connectParent?.parent.Position ?? IntVec3.Invalid;
                        IntVec3 exp = MAt(w.tdx, w.tdz, w.tk, map);
                        Result($"lamp {lc} f{w.k + 1} hooked to {exp} f{w.tk + 1}", p == exp, $"hooked to {p} f{MFloor(p, map)}");
                    }
                    Result("no printed wire longer than a hookup (the game's print skipped across floors)", mLongPrints == 0, $"{mLongPrints} long prints");
                    mShot = 0;
                    Wait(5, "m228_shot");
                    return true;
                }

                // ---- deathrest
                case "m228_rest":
                {
                    Building_Bed casket = MDAt(2, 3, 0, map).GetFirstThing<Building_Bed>(map);
                    Job job = JobMaker.MakeJob(JobDefOf.Deathrest, casket);
                    job.forceSleep = true;
                    mRester.jobs.StartJob(job, JobCondition.InterruptForced);
                    SeedTicks("m228_rest");
                    TickUntil(6000, () => mRester.Deathresting && mRester.CurrentBed() == casket, "m228_resting", 10);
                    return true;
                }
                case "m228_resting":
                {
                    tickWatch = null;
                    Result("the colonist deathrests in the casket (Deathrest job)", mRester.Deathresting, $"job {mRester.CurJobDef?.defName}, bed {mRester.CurrentBed()}");
                    Thing b = Place(ThingDefOf.Battery, null, MDAt(7, 3, 1, map), map, Rot4.North);
                    CompPowerBattery cb = b.TryGetComp<CompPowerBattery>();
                    cb?.AddEnergy(cb.Props.storedEnergyMax);
                    mPump = Place(DefDatabase<ThingDef>.GetNamed("Hemopump"), null, MDAt(4, 3, 1, map), map, Rot4.East);
                    mPump.TryGetComp<CompRefuelable>()?.Refuel(5f);
                    CompDeathrestBindable bc = mPump.TryGetComp<CompDeathrestBindable>();
                    Gene_Deathrest gene = mRester.genes?.GetFirstGeneOfType<Gene_Deathrest>();
                    bool bound = bc?.BoundPawn == mRester;
                    Info($"hemopump {mPump.Position} f{MFloor(mPump.Position, map)} spawned while deathresting: bound by the game {bound}; can bind {gene?.CanBindToBindable(bc)}; " +
                        $"rooms {mRester.GetRoom()?.ID} / {mPump.GetRoom()?.ID}");
                    if (!bound && gene != null && bc != null)
                    {
                        // no game flow binds across floors (Gene_Deathrest.CanUseBindableNow: same room and line of sight):
                        // bound here to test the drawing only
                        // and the game takes a bound building into use only in the same room with line of sight
                        // (Gene_Deathrest.BoundComps -> CanUseBindableNow, every draw): the test lets the pump count
                        mForcePump = true;
                        gene.BindTo(bc);
                        AccessTools.Field(typeof(Gene_Deathrest), "cachedBoundComps").SetValue(gene, null);   // (the game resets it in TryLinkToNearbyDeathrestBuildings)
                        Info("  bound by the test (Gene_Deathrest.BindTo): the drawing check only");
                    }
                    SeedTicks("m228_pump");
                    StartTicks(300, "m228_rest_check");
                    return true;
                }
                case "m228_rest_check":
                {
                    CompDeathrestBindable bc = mPump.TryGetComp<CompDeathrestBindable>();
                    Result("the floor-2 hemopump is bound, powered and fuelled (the hose shows)", bc.BoundPawn == mRester && bc.CanIncreasePresence,
                        $"bound {bc.BoundPawn}, power {mPump.TryGetComp<CompPowerTrader>()?.PowerOn}, fuel {mPump.TryGetComp<CompRefuelable>()?.Fuel}");
                    Gene_Deathrest gene = mRester.genes?.GetFirstGeneOfType<Gene_Deathrest>();
                    Info($"hose state: mustBeLaying {bc.Props.mustBeLayingInToBind}, same map {mRester.Map == mPump.Map}, deathresting {mRester.Deathresting}, in bound comps {gene?.BoundComps.Contains(bc)}, presence {bc.CanIncreasePresence}, hose calls so far {CrossFloorWires.hoseCalls}");
                    Wait(5, "m228_shot");
                    return true;
                }

                // ---- shots
                case "m228_shot":
                {
                    if (mShot >= mShots.Length)
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                        Result("no game's ghost wire longer than a hookup was drawn", mLongGhosts == 0, $"{mLongGhosts}");
                        // 3D off (multi-floor maps are locked to 3D; should not happen): nothing drawn, nothing thrown
                        ShotAt(map, 1, new IntVec3(mO.x + 8, 0, mO.z + 3), 16f);
                        mOffErrors = errors;
                        BrView3D(false);
                        Wait(30, "m228_off");
                        return true;
                    }
                    MShotDef s = mShots[mShot];
                    if (s.n.StartsWith("3d_deathrest") && mRester != null && mPump == null)
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                        stage = "m228_rest";
                        return true;
                    }
                    ShotAt(map, s.view, s.g, s.size);
                    Wait(20, "m228_aim");
                    return true;
                }
                case "m228_off":
                {
                    bool none = CrossFloorWires.lastFrame < Time.frameCount - 5;
                    Result("3D view off: no cross-floor wire drawn, no error", none && errors == mOffErrors, $"last drawn frame {CrossFloorWires.lastFrame}, now {Time.frameCount}, errors {errors - mOffErrors}");
                    BrView3D(true);
                    Patches.CameraDriver_Update_Patch.AllowAnnexView = false;
                    WatchCheck(map, "m228 end");
                    Finish();
                    return true;
                }
                case "m228_aim":
                {
                    MShotDef s = mShots[mShot];
                    if (s.aim == MAim.Lamp)
                    {
                        fakeMouse = ScreenOf(new IntVec3(mO.x + s.gx, 0, mO.z + s.gz), s.gk);
                        Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(MLamp) ?? new Designator_Build(MLamp));
                    }
                    else if (s.aim == MAim.Conduit)
                    {
                        fakeMouse = new Vector2(4f, 4f);
                        ThingDef conduit = DefDatabase<ThingDef>.GetNamed("PowerConduit");
                        Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(conduit) ?? new Designator_Build(conduit));
                    }
                    else
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                    }
                    Wait(30, "m228_take");
                    return true;
                }
                case "m228_take":
                {
                    MShotDef s = mShots[mShot];
                    bool fresh = CrossFloorWires.lastFrame >= Time.frameCount - 2;
                    mSegs.Clear();
                    if (fresh)
                    {
                        mSegs.AddRange(CrossFloorWires.LastFrame);
                    }
                    Info($"shot {s.n}: hose calls {CrossFloorWires.hoseCalls}, view f{s.view + 1}, mouse {UI.MouseCell()} f{MFloor(UI.MouseCell(), map)}, segments drawn ({(fresh ? "this frame" : "none lately")}): {MSegText(mSegs)}");
                    MCheckShot(s, map);
                    ZCapture("m228_" + s.n);
                    mShot++;
                    Wait(2, "m228_shot");
                    return true;
                }
            }
            return false;
        }

        private static void MCheckShot(MShotDef s, Map map)
        {
            float wAlt = AltitudeLayer.SmallWire.AltitudeFor();
            float oAlt = AltitudeLayer.MapDataOverlay.AltitudeFor();
            int wires = mSegs.Count(x => x.kind == "wire"), overlays = mSegs.Count(x => x.kind == "overlay");
            List<CrossFloorWires.Segment> ghosts = mSegs.Where(x => x.kind == "ghost").ToList();
            List<CrossFloorWires.Segment> hoses = mSegs.Where(x => x.kind == "hose").ToList();
            bool hidden = s.view == 0;
            // every segment: the lower end on its floor; the upper on its floor or, hidden, cut at its slab
            int bad = 0;
            foreach (CrossFloorWires.Segment x in mSegs)
            {
                float alt = x.kind == "wire" || x.kind == "hose" ? wAlt : oAlt;
                bool lowOk = Mathf.Abs(x.a.y - MSlabY(x.floorA, alt)) < 0.01f;
                bool highOk = Mathf.Abs(x.b.y - (hidden ? MCutY(s.view + 1, alt) : MSlabY(x.floorB, alt))) < 0.01f && x.cut == hidden;
                bool inGround = x.a.z < ProjectZ.GroundZ(map) && x.b.z < ProjectZ.GroundZ(map);
                bool shortRun = new Vector2(x.b.x - x.a.x, x.b.z - x.a.z).magnitude < 9f;
                if (!(lowOk && highOk && inGround && shortRun))
                {
                    bad++;
                    Result($"{s.n}: {x.kind} segment between its floors as drawn", false, MSegText(new[] { x }));
                }
            }
            Result($"{s.n}: every segment from its floor to the other floor{(hidden ? "'s slab (cut)" : "")}, in drawn space, hookup length", bad == 0, $"{mSegs.Count} segments");
            if (s.n.StartsWith("3d_deathrest"))
            {
                if (hidden)
                {
                    // the hose is drawn by the bound building (CompDeathrestBindable.PostDraw): on a hidden floor it is not drawn at all
                    Result($"{s.n}: the pump's floor hidden: no hose (no floating end)", hoses.Count == 0, MSegText(hoses));
                }
                else
                {
                    Result($"{s.n}: the hose drawn once between the floors", hoses.Count == 1 && !hoses[0].cut, MSegText(hoses));
                }
                return;
            }
            Result($"{s.n}: {MCross} grey wires across floors{(hidden ? ", cut at the slab" : "")}", wires == MCross, $"{wires}");
            bool overlayOn = s.aim != MAim.None;
            Result($"{s.n}: overlay lines across floors {(overlayOn ? MCross : 0)}", overlays == (overlayOn ? MCross : 0), $"{overlays}");
            if (s.aim == MAim.Lamp)
            {
                IntVec3 cell = UI.MouseCell();
                CompPower t = PowerConnectionMaker.BestTransmitterForConnector(cell, map);
                bool across = t != null && MFloor(t.parent.Position, map) != MFloor(cell, map);
                if (across)
                {
                    bool ok = ghosts.Count == 1;
                    if (ok)
                    {
                        CrossFloorWires.Segment g = ghosts[0];
                        IntVec3 gc = ProjectZ.GroundCellOf(cell, map);
                        Vector3 user = g.floorA == MFloor(cell, map) - 1 ? g.a : g.b;
                        ok = Mathf.Abs(user.x - (gc.x + 0.5f)) < 0.6f && Mathf.Abs(user.z - (gc.z + 0.5f)) < 0.6f;
                    }
                    Result($"{s.n}: the ghost's wire drawn from the ghost {cell} f{MFloor(cell, map)} to {t.parent.def.defName} f{MFloor(t.parent.Position, map)}{(hidden ? ", cut at the slab" : "")}", ok, MSegText(ghosts));
                }
                else
                {
                    Result($"{s.n}: own-floor ghost wire left to the game ({cell} f{MFloor(cell, map)} -> {t?.parent.Position})", ghosts.Count == 0, MSegText(ghosts));
                }
            }
        }

        private static void MField(Map map, CellRect field)
        {
            ExClear(map, field);
            foreach (IntVec3 c in field)
            {
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                map.fogGrid.Unfog(c);
            }
        }

        private static void MSlab(Map map, IntVec3 o, int x0, int x1, int z0, int z1, int[] colX, int[] colZ)
        {
            for (int dx = x0; dx <= x1; dx++)
            {
                for (int dz = z0; dz <= z1; dz++)
                {
                    IntVec3 u = Levels.Cell(new IntVec3(o.x + dx, 0, o.z + dz), 1, map);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                    map.fogGrid.Unfog(u);
                }
            }
            ThingDef column = DefDatabase<ThingDef>.GetNamed("Column");
            foreach (int dx in colX)
            {
                foreach (int dz in colZ)
                {
                    Place(column, ThingDefOf.WoodLog, new IntVec3(o.x + dx, 0, o.z + dz), map);
                }
            }
        }

        private static void MSetup(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2)
            {
                Result("m228 setup", false, $"needs floors (-projectzfloors=3); floors {an?.floors}");
                Finish();
                return;
            }
            MProbePatch();
            Result("cross-floor wire patches applied", FailSoft.IsApplied(nameof(PrintWirePieceConnecting_CrossFloor_Patch)) && FailSoft.IsApplied(nameof(MapDrawer_CrossFloorWires_Patch))
                && FailSoft.IsApplied(nameof(RenderAnticipatedWire_CrossFloor_Patch)) && FailSoft.IsApplied(nameof(DeathrestHose_CrossFloor_Patch)), "");
            MField(map, new CellRect(mO.x - 8, mO.z - 8, 36, 26));
            MField(map, new CellRect(mD.x - 4, mD.z - 8, 18, 22));
            map.roofCollapseBuffer.Clear();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Faction != Faction.OfPlayer)
                {
                    p.Destroy();
                }
            }
            // ---- W
            MSlab(map, mO, -1, 19, 1, 11, new[] { -1, 5, 11, 19 }, new[] { 1, 6, 11 });
            ThingDef conduit = DefDatabase<ThingDef>.GetNamed("PowerConduit");
            for (int dx = 0; dx <= 6; dx++)
            {
                Place(conduit, null, MAt(dx, 0, 0, map), map);
            }
            foreach (int dx in new[] { 13, 15, 17 })
            {
                Place(ThingDefOf.Battery, null, MAt(dx, 8, 1, map), map, Rot4.North);
            }
            foreach (int dx in new[] { 1, 3, 5 })
            {
                Place(MLamp, null, MAt(dx, 4, 1, map), map);
            }
            Place(MLamp, null, MAt(15, 4, 1, map), map);
            foreach (int dx in new[] { 14, 16 })
            {
                Place(MLamp, null, MAt(dx, 5, 0, map), map);
            }
            // ---- D: casket on the ground, floor-2 slab over it
            MSlab(map, mD, -1, 10, 1, 8, new[] { -1, 10 }, new[] { 1, 8 });
            Place(DefDatabase<ThingDef>.GetNamed("DeathrestCasket"), ThingDefOf.Steel, MDAt(2, 3, 0, map), map, Rot4.North);
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Draft(cols[i], false);
                Teleport(cols[i], MAt(8 + i, -5, 0, map));
            }
            mRester = cols.Count > 1 ? cols[cols.Count - 1] : null;
            mPump = null;
            if (mRester != null)
            {
                mRester.genes.AddGene(DefDatabase<GeneDef>.GetNamed("Deathrest"), true);
                Teleport(mRester, MDAt(2, 0, 0, map));
            }
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 75;
            GenSpawn.Spawn(steel, MAt(12, -4, 0, map), map);
            SetWork(map, false);
            mShots = new[]
            {
                MS("3d_f2_ghost_down", 1, MAim.Lamp, 3, 6, 1),
                MS("3d_f2_ghost_own", 1, MAim.Lamp, 15, 6, 1),
                MS("3d_ground_ghost_up", 1, MAim.Lamp, 18, 2, 0),
                MS("3d_ground_ghost_up_hidden", 0, MAim.Lamp, 18, 2, 0),
                MS("3d_f2_overlay_off", 1, MAim.None),
                MS("3d_f2_overlay_on", 1, MAim.Conduit),
                MS("3d_ground_hidden_overlay_on", 0, MAim.Conduit),
                MS("3d_ground_hidden_overlay_off", 0, MAim.None),
                MS("3d_f2_overlay_on_wide", 1, MAim.Conduit, size: 40f),
                MS("3d_deathrest_f2", 1, MAim.None, deathrest: true, size: 10f),
                MS("3d_deathrest_ground_hidden", 0, MAim.None, deathrest: true, size: 10f),
            };
            Info($"m228: W {mO}, D {mD}, floors {an.floors}, ground rows {ProjectZ.GroundZ(map)}, stride {an.stride}, map {map.Size}, storey {IsoRimBridge.StoreyHeight}, squash {IsoRimBridge.Squash}, rester {mRester}");
            AnnexWatch.Reset();
            SeedTicks("m228_setup");
            StartTicks(120, "m228_build");
        }
    }
}
