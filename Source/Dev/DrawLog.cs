using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Test only: logs, for frames while Frames > 0, every mesh drawn whose position lies in Area (raw world position,
    // before any floor mapping), with its material, shader and texture. Used to find what draws an artefact.
    public static class DrawLog
    {
        public static int Frames;
        public static CellRect Area;
        public static CellRect Area2;
        public static int TempRoofs;          // the game's temporary roof tiles drawn in the area
        public static int TempRoofsUpright;   // ... of them stood up (the striped ghost)
        private static int lastFrame = -1;
        private static int lines;

        public static void Patch(Harmony h)
        {
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (!m.IsStatic || m.ContainsGenericParameters)
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                bool matrix = false, array = false, list = false, material = false;
                foreach (ParameterInfo p in ps)
                {
                    matrix |= p.Name == "matrix" && p.ParameterType == typeof(Matrix4x4);
                    array |= p.Name == "matrices" && p.ParameterType == typeof(Matrix4x4[]);
                    list |= p.Name == "matrices" && p.ParameterType == typeof(List<Matrix4x4>);
                    material |= p.Name == "material";
                }
                try
                {
                    if (m.Name == "DrawMeshCore" && matrix)
                    {
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DrawLog), nameof(Single)) { priority = Priority.First });
                    }
                    else if (m.Name == "DrawMeshInstanced" && array && ps.Length >= 9)
                    {
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DrawLog), nameof(Instanced)) { priority = Priority.First });
                    }
                }
                catch (Exception e)
                {
                    Log.Message("[PZTest] draw log patch failed " + m + ": " + e.Message);
                }
            }
        }

        private static bool On()
        {
            if (Frames <= 0)
            {
                return false;
            }
            if (Time.frameCount != lastFrame)
            {
                if (lastFrame >= 0)
                {
                    Frames--;
                }
                lastFrame = Time.frameCount;
            }
            return Frames > 0 && lines < 4000;
        }

        private static bool In(Vector3 p)
        {
            IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z));
            return Area.Contains(c) || Area2.Contains(c);
        }

        private static string Describe(Mesh mesh, Material material)
        {
            string tex = "-";
            try
            {
                tex = material?.mainTexture?.name ?? "-";
            }
            catch (Exception)
            {
            }
            return $"mesh {mesh?.name} ({mesh?.vertexCount} v) mat {material?.name} shader {material?.shader?.name} tex {tex} queue {material?.renderQueue}";
        }

        public static void Single(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            if (!On())
            {
                return;
            }
            Vector3 p = matrix.GetColumn(3);
            if (!In(p))
            {
                return;
            }
            lines++;
            if (material != null && material.name != null && material.name.Contains("TempRoof"))
            {
                TempRoofs++;
                if (Mathf.Abs(matrix.m11) > 2f || Mathf.Abs(matrix.m21) > 2f)
                {
                    TempRoofsUpright++;
                }
            }
            TestRunner.Out($"draw {Time.frameCount} at {p} {Describe(mesh, material)} scale {matrix.lossyScale}");
        }

        public static void Instanced(Mesh mesh, Material material, Matrix4x4[] matrices, int count)
        {
            if (!On() || matrices == null)
            {
                return;
            }
            for (int i = 0; i < count && i < matrices.Length; i++)
            {
                Vector3 p = matrices[i].GetColumn(3);
                if (In(p))
                {
                    lines++;
                    TestRunner.Out($"draw-inst {Time.frameCount} [{i}/{count}] at {p} {Describe(mesh, material)}");
                }
            }
        }
    }
}
