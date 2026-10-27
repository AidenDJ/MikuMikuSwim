using System;
using System.Collections.Generic;
using UnityEngine;

namespace MikuPlayerReplacer
{
    internal static class CinematicArmGeometry
    {
        // Follow ancestry so fingers, twist helpers and imported sleeve chains
        // work without any model-specific material or bone names.
        internal static bool[] ArmBones(int[] parents, string[] names)
        {
            bool[] result = new bool[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int at = i;
                for (int guard = 0; at >= 0 && at < names.Length && guard < names.Length; guard++)
                {
                    string name = names[at];
                    if (name == "shoulder_L" || name == "shoulder_R"
                        || name == "elbow_L" || name == "elbow_R"
                        || name == "hand_L" || name == "hand_R")
                    { result[i] = true; break; }
                    at = parents[at];
                }
            }
            return result;
        }

        internal static float[] Influence(BoneWeight[] weights, bool[] arms)
        {
            float[] result = new float[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                BoneWeight w = weights[i];
                result[i] = Weight(w.boneIndex0, w.weight0, arms)
                    + Weight(w.boneIndex1, w.weight1, arms)
                    + Weight(w.boneIndex2, w.weight2, arms)
                    + Weight(w.boneIndex3, w.weight3, arms);
            }
            return result;
        }

        private static float Weight(int index, float weight, bool[] arms)
        { return index >= 0 && index < arms.Length && arms[index] ? weight : 0f; }

        internal static int[] Select(int[] triangles, float[] influence)
        {
            List<int> selected = new List<int>();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                // Retain the blended shoulder boundary without exposing the
                // torso triangles that caused the camera interior problem.
                if (Math.Max(influence[a], Math.Max(influence[b], influence[c])) < 0.5f)
                    continue;
                selected.Add(a); selected.Add(b); selected.Add(c);
            }
            return selected.ToArray();
        }
    }
}
