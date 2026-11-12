using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using Verse;

namespace ProjectZ.Dev
{
    // Test only (-projectzrandtick=N): the call stack of every random draw during game tick N, to renders/<tag>/randtick.txt.
    // With -projectzseeddebug (a hash of the pawns' places per stage) it finds where two runs of a mode part and what drew
    // from the random state differently (pztests 2026-11-11: the fish shadows, now kept out of the stream by IsoRim's QuickTestSeed).
    public static partial class TestRunner
    {
        private static int randTick = -1;
        private static readonly List<string> randLines = new List<string>();

        internal static void RandProbeInit(Harmony h)
        {
            if (!GenCommandLine.TryGetCommandLineArg("projectzrandtick", out string s) || !int.TryParse(s, out randTick))
            {
                return;
            }
            foreach (string m in new[] { "get_Value", "get_Int" })
            {
                h.Patch(AccessTools.Method(typeof(Rand), m), postfix: new HarmonyMethod(typeof(TestRunner), nameof(RandPostfix)));
            }
        }

        private static void RandPostfix()
        {
            if (Current.Game == null || Find.TickManager == null || Find.TickManager.TicksGame != randTick || randLines.Count > 4000)
            {
                return;
            }
            StackTrace st = new StackTrace(2, false);
            randLines.Add(string.Join(" < ", st.GetFrames().Take(7).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
            if (randLines.Count % 50 == 0)
            {
                RandFlush();
            }
        }

        internal static void RandFlush()
        {
            if (randLines.Count > 0 && outDir != null)
            {
                File.AppendAllLines(Path.Combine(outDir, "randtick.txt"), randLines);
                randLines.Clear();
            }
        }
    }
}
