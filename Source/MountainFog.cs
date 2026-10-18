using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Tall-mountain fog after generation (isorim-pz-mountains-fog-plan.md §2.2, user 2026-10-18). Generation reveals only a
    // one-cell rim of each top (Mountains.BandFog); the rest of a plateau opens "where the player's pawns can walk to":
    //   (a) stairs completed onto a band (Building_Stairs.SpawnSetup);
    //   (b) a player pawn arriving on a band without stairs: a jump, a drop pod, a shuttle, a skip (Pawn.SpawnSetup and
    //       Pawn.Notify_Teleported; user Q9);
    //   (c) a player pawn stepping onto a band cell next to fog (user Q1; vanilla's door rule on open band floors).
    // Reveal walks the band's walkable cells connected to the start (4-way, its own band only) and floods vanilla's fog
    // from every fogged non-blocking cell next to them (FloodFillerFog.FloodUnfog: the fogged open cells, then the rock
    // faces 8-adjacent to them). Ores and inner rock stay fogged; mining is vanilla. No letter (user Q5).
    // Player pawns are vanilla's own set (Pawn.SpawnSetup's unfog): the player's faction (colonists, tame animals, mechs,
    // slaves) or player controlled; not guests, prisoners, wild animals or enemies.
    // Every path asks Mountains.On first: maps without tall mountains are untouched.
    public static class MountainFog
    {
        // last Reveal and totals, for the tests
        public static int LastWalked, LastFloods, Reveals, StepReveals;
        public static double LastMs, MaxMs;

        private static int[] mark;
        private static int markGen;
        private static readonly Queue<IntVec3> queue = new Queue<IntVec3>();
        private static bool busy;

        public static bool PlayerPawn(Pawn p) => p != null && (p.Faction == Faction.OfPlayer || p.IsPlayerControlled);

        private static bool Blocker(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.MakeFog;
        }

        // The band area the player can walk to from `from` (a cell of an upper band of a tall-mountain map) is revealed,
        // and its level counts as reached (the floor HUD, ShownLevels).
        public static void Reveal(IntVec3 from, Map map)
        {
            if (busy || map == null || !from.IsValid || !from.InBounds(map) || !Mountains.On(map) || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            int L = Levels.LevelOf(from, map);
            if (L == Levels.None || L < 1)
            {
                return;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            a.MarkReached(L);
            busy = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int walked = 0, floods = 0;
            try
            {
                FogGrid fog = map.fogGrid;
                CellIndices ci = map.cellIndices;
                if (fog.IsFogged(from) && !Blocker(from, map))
                {
                    FloodFillerFog.FloodUnfog(from, map);
                    floods++;
                }
                if (fog.IsFogged(from))
                {
                    return;
                }
                int n = ci.NumGridCells;
                if (mark == null || mark.Length != n)
                {
                    mark = new int[n];
                    markGen = 0;
                }
                if (++markGen == int.MaxValue)
                {
                    System.Array.Clear(mark, 0, n);
                    markGen = 1;
                }
                int band = Levels.BandOfCell(from, map);
                queue.Clear();
                mark[ci.CellToIndex(from)] = markGen;
                queue.Enqueue(from);
                while (queue.Count > 0)
                {
                    IntVec3 c = queue.Dequeue();
                    walked++;
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 nb = c + GenAdj.CardinalDirections[d];
                        if (!nb.InBounds(map))
                        {
                            continue;
                        }
                        int i = ci.CellToIndex(nb);
                        if (mark[i] == markGen || Levels.BandOfCell(nb, map) != band)
                        {
                            continue;
                        }
                        mark[i] = markGen;
                        if (fog.IsFogged(i))
                        {
                            if (Blocker(nb, map))
                            {
                                continue;   // rock: a face, only unfogged by a flood next to it (vanilla)
                            }
                            FloodFillerFog.FloodUnfog(nb, map);
                            floods++;
                        }
                        if (nb.Walkable(map))
                        {
                            queue.Enqueue(nb);
                        }
                    }
                }
            }
            finally
            {
                busy = false;
                queue.Clear();
                LastWalked = walked;
                LastFloods = floods;
                LastMs = sw.Elapsed.TotalMilliseconds;
                MaxMs = System.Math.Max(MaxMs, LastMs);
                Reveals++;
                if (floods > 0)
                {
                    ProjectZ.DebugLog($"mountain fog: revealed from {from} (level {L}): {walked} walkable cells, {floods} floods, {LastMs:0.00} ms");
                }
            }
        }

        // (c) A player pawn on an upper band of a tall-mountain map with fog next to it (4-way, not rock) or under it.
        public static void Stepped(Pawn pawn)
        {
            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 c = pawn.Position;
            if (a == null || !a.tallMountains || c.z < a.groundZ || !PlayerPawn(pawn))
            {
                return;
            }
            FogGrid fog = map.fogGrid;
            bool near = fog.IsFogged(c);
            for (int d = 0; d < 4 && !near; d++)
            {
                IntVec3 nb = c + GenAdj.CardinalDirections[d];
                near = nb.InBounds(map) && fog.IsFogged(nb) && !Blocker(nb, map);
            }
            if (near)
            {
                StepReveals++;
                Reveal(c, map);
            }
        }
    }

    // (b) A player pawn spawned on a band (drop pod and shuttle exits, a jump's landing: PawnFlyer spawns the pawn again).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Pawn_SpawnSetup_MountainFog_Patch
    {
        public static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || map == null || !__instance.Spawned || __instance.Position.z < ProjectZ.GroundZ(map) || !MountainFog.PlayerPawn(__instance))
            {
                return;
            }
            MountainFog.Reveal(__instance.Position, map);
        }
    }

    // (b) A player pawn moved without a spawn (skip and farskip, user Q9; anything else that teleports a pawn).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Notify_Teleported))]
    public static class Pawn_Notify_Teleported_MountainFog_Patch
    {
        public static void Postfix(Pawn __instance)
        {
            Map map = __instance.MapHeld;
            if (map == null || !__instance.Spawned || __instance.Position.z < ProjectZ.GroundZ(map) || !MountainFog.PlayerPawn(__instance))
            {
                return;
            }
            MountainFog.Reveal(__instance.Position, map);
        }
    }

    // (c) Every path step (the stairs step patch is TryEnterNextPathCell_Patch; this one only reads where the pawn is now).
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class TryEnterNextPathCell_MountainFog_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Postfix(Pawn_PathFollower __instance)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn != null && pawn.Spawned)
            {
                MountainFog.Stepped(pawn);
            }
        }
    }
}
