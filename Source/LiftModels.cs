using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // The 3D look of the elevator's shaft, the improvised ladders and the mechanoids' lift (models approved by the user,
    // Grok 2026-10-20: Models/pz_elevator_*.obj, pz_ladder_*.obj, pz_lift_*.obj with their own pz_lifts.mtl: fixed
    // colours, the lift's lamp faces glow_ = lit by IsoRim). Every model is built facing north (-z of the file), one
    // storey (1.67) a stacked piece, the cell's centre at the origin.
    //   - the elevator's base: IsoRim's ModelDef (IsoRim/Defs/PZ_Models.xml, baked like any building, turned to the door
    //     side). Over it, drawn here each frame: a shaft segment on every level up to the shaft's top (Building_Elevator
    //     .LinkedTop: a cap ends it), then the top piece on the last segment; under a cap (the shaft ends below the map's
    //     top level, a floor or building over it) the top piece hangs inside the last storey instead of poking through.
    //     The rider's platform: ElevatorDraw.
    //   - a ladder (raiders' or the player's emergency one): the foot and one segment per storey up the column from the
    //     foot, the top hooks at the top's level, on the column's cell of each level; the lift: base on the foot's level,
    //     a mast segment on each level up to the top, the top piece at the top's level. Turned so the model's north is
    //     the side the ladder leans on (toward the top cell). The building's own sprite is not printed while these show
    //     (Building_SiegeLadder.Print, Replaces).
    // Each piece is drawn on the rows of its own level at its height over that floor (Api.DrawModel, true space as IsoRim's
    // floors expect), so a hidden level hides its pieces as it hides its pawns. A level faded while peeking
    // (Api.PeekUpperFloors) would get them solid (DrawModel draws outside a thing's draw and takes no floor fade), so
    // levels above the viewed one are left out, as the power risers do (RiserView). IsoRim Api >= 8 only, behind
    // no-inline methods; 3D or IsoRim's models off: nothing here (the 2D placeholders show).
    public static class LiftModels
    {
        public const int ElevatorShaft = 0, ElevatorTop = 1, Platform = 2, LadderFoot = 3, LadderSegment = 4, LadderTop = 5, LiftBase = 6, LiftMast = 7, LiftTop = 8;

        private static readonly string[] Files =
        {
            "pz_elevator_shaft", "pz_elevator_top", "pz_elevator_platform", "pz_ladder_foot", "pz_ladder_segment", "pz_ladder_top",
            "pz_lift_base", "pz_lift_mast", "pz_lift_top",
        };

        // The platform's deck top over its origin (pz_elevator_platform.obj: the floor plate y 0.02..0.07).
        public const float PlatformDeck = 0.07f;

        // pz_elevator_top.obj's height.
        public const float ElevatorTopHeight = 0.265f;

        private static readonly object[] handles = new object[Files.Length];
        private static bool loaded;

        // (tests) pieces drawn in the last frame that drew, and that frame; true: draw levels above the viewed one too.
        public static int Pieces, LastFrame = -1;
        public static bool DrawAboveView;

        // A level's pieces are drawn: the viewed level and those under it.
        public static bool LevelDrawn(int level, int view) => DrawAboveView || level <= view;

        // The model of a part (an IsoRim.ModelHandle; null if it did not load). Only with IsoRim Api >= 8.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static object Handle(int part)
        {
            if (!loaded)
            {
                loaded = true;
                ModContentPack mod = LoadedModManager.GetMod<ProjectZMod>()?.Content;
                for (int i = 0; i < Files.Length; i++)
                {
                    handles[i] = IsoRim.Api.LoadModel(mod, "Models/" + Files[i] + ".obj");
                }
            }
            return handles[part];
        }

        // The ladder's or lift's models stand in for its sprite now (its link known, IsoRim drawing models).
        public static bool Replaces(Building_SiegeLadder b)
        {
            return b != null && b.top.IsValid && b.foot.IsValid && b.span > 0 && IsoRimBridge.Present && IsoRimBridge.ApiVersion >= 8 && ModelsOn();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ModelsOn() => IsoRim.Api.ModelsOn;

        // Each frame (LinkSteps.MapComponentUpdate).
        public static void Draw(Map map)
        {
            if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < 8 || map != Find.CurrentMap || ProjectZ.Annex(map) == null)
            {
                return;
            }
            DrawInner(map);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DrawInner(Map map)
        {
            if (!IsoRim.Api.ModelsOn)
            {
                return;
            }
            int view = FloorView.Current;
            float storey = IsoRimBridge.StoreyHeight;
            int n = 0;
            if (ElevatorDefOf.PZ_Elevator != null)
            {
                List<Thing> els = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator);
                for (int i = 0; i < els.Count; i++)
                {
                    if (els[i] is Building_Elevator e && e.Spawned && e.Ready)
                    {
                        n += Elevator(e, map, view, storey);
                    }
                }
            }
            n += Ladders(PZDefOf.PZ_SiegeLadder, map, view, storey);
            n += Ladders(PZDefOf.PZ_EmergencyLadder, map, view, storey);
            n += Ladders(PZDefOf.PZ_SiegeLift, map, view, storey);
            Pieces = n;
            LastFrame = Time.frameCount;
        }

        private static int Elevator(Building_Elevator e, Map map, int view, float storey)
        {
            IntVec3 g = Levels.Ground(e.Position, map);
            float yaw = e.Rotation.AsAngle;
            int top = Mathf.Max(e.baseLevel, e.LinkedTop);
            int n = 0;
            for (int L = e.baseLevel + 1; L <= top; L++)
            {
                n += Piece(ElevatorShaft, g, L, 0f, yaw, view, map);
            }
            // on the last segment; under a cap (a floor or building over the shaft's top) inside the last storey
            float y = top < Levels.Max(map) ? storey - ElevatorTopHeight : storey;
            n += Piece(ElevatorTop, g, top, y, yaw, view, map);
            return n;
        }

        private static int Ladders(ThingDef def, Map map, int view, float storey)
        {
            if (def == null)
            {
                return 0;
            }
            List<Thing> list = map.listerThings.ThingsOfDef(def);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is Building_SiegeLadder b) || !b.Spawned || !b.top.IsValid || !b.foot.IsValid || b.span <= 0)
                {
                    continue;
                }
                IntVec3 g = Levels.Ground(b.foot, map);
                IntVec3 d = Levels.Ground(b.top, map) - g;
                if (Mathf.Abs(d.x) + Mathf.Abs(d.z) != 1)
                {
                    continue;
                }
                float yaw = Rot4.FromIntVec3(d).AsAngle;   // the model's north: the edge it leans on
                int f = Levels.LevelOf(b.foot, map), t = Levels.LevelOf(b.top, map);
                if (b.Kind == LinkKind.Lift)
                {
                    n += Piece(LiftBase, g, f, 0f, yaw, view, map);
                    for (int L = f + 1; L < t; L++)
                    {
                        n += Piece(LiftMast, g, L, 0f, yaw, view, map);
                    }
                    n += Piece(LiftTop, g, t, 0f, yaw, view, map);
                }
                else
                {
                    n += Piece(LadderFoot, g, f, 0f, yaw, view, map);
                    for (int L = f; L < t; L++)
                    {
                        n += Piece(LadderSegment, g, L, 0f, yaw, view, map);
                    }
                    n += Piece(LadderTop, g, t, 0f, yaw, view, map);
                }
            }
            return n;
        }

        // One piece on column g's cell of level L, y over that floor.
        private static int Piece(int part, IntVec3 g, int L, float y, float yaw, int view, Map map)
        {
            if (!LevelDrawn(L, view) || !(Handle(part) is IsoRim.ModelHandle m))
            {
                return 0;
            }
            IntVec3 c = Levels.Cell(g, L, map);
            if (!c.IsValid || !c.InBounds(map))
            {
                return 0;
            }
            IsoRim.Api.DrawModel(m, new Vector3(c.x + 0.5f, y, c.z + 0.5f), yaw, Vector3.one, Color.white);
            return 1;
        }
    }
}
