using System;
using System.IO;
using UnityEngine;
namespace MikuPlayerReplacer
{
    internal static class CinematicArmProbe
    {
        private static void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); }
        public static void Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            bool[] arms = CinematicArmGeometry.ArmBones(new[]{-1,0,1,2,3,0,6},
                new[]{"chest","shoulder_R","elbow_R","finger","sleeve", "neck", "cycle"});
            Check(!arms[0] && arms[1] && arms[2] && arms[3] && arms[4]
                && !arms[5] && !arms[6], "Arm ancestry, sleeve or cycle classification failed");
            Check(CinematicArmGeometry.Select(new[]{0,1,2}, new[]{0f,0f,0f}).Length == 0,
                "Torso must remain hidden");
            Check(CinematicArmGeometry.Select(new[]{0,1,2}, new[]{0f,.5f,0f}).Length == 3,
                "Blended shoulder must remain visible");
            Check(CinematicArmGeometry.Select(new int[0], new float[0]).Length == 0,
                "User-hidden material must remain hidden");
            int models = 0;
            foreach (string path in File.ReadAllLines(args[0]))
            {
                ModelData data = PmxConverter.Convert(PmxReader.Read(path), "cinematic-arm-probe");
                int[] parents = new int[data.Bones.Count];
                string[] names = new string[parents.Length];
                for (int i = 0; i < names.Length; i++)
                { names[i] = data.Bones[i].Name; parents[i] = data.Bones[i].Parent; }
                arms = CinematicArmGeometry.ArmBones(parents, names);
                int selected = 0, excluded = 0;
                bool left = false, right = false;
                foreach (MeshData mesh in data.Meshes)
                {
                    if (mesh.Role != MeshData.RoleBody) continue;
                    float[] influence = CinematicArmGeometry.Influence(mesh.Weights, arms);
                    foreach (int[] indices in mesh.Indices)
                    {
                        int[] kept = CinematicArmGeometry.Select(indices, influence);
                        selected += kept.Length / 3;
                        excluded += (indices.Length - kept.Length) / 3;
                        foreach (int vertex in kept)
                        {
                            BoneWeight w = mesh.Weights[vertex];
                            int at = w.boneIndex0;
                            for (int guard = 0; at >= 0 && at < names.Length && guard < names.Length; guard++)
                            {
                                if (names[at] == "shoulder_L") left = true;
                                if (names[at] == "shoulder_R") right = true;
                                at = parents[at];
                            }
                        }
                    }
                }
                Check(selected > 0 && excluded > 0 && left && right,
                    "Missing left/right arms or exposed whole torso: " + path);
                Console.WriteLine(Path.GetFileName(path) + " arms=" + selected + " torsoExcluded=" + excluded + " PASS");
                models++;
            }
            Check(models > 0, "No model checks executed");
            Console.WriteLine("CINEMATIC_ARM_CHECKS=PASS models=" + models);
        }
    }
}
