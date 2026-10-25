using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

// Uses the production PMX reader/converter with data-only containers. No game
// process, Animator, scene, or model asset is modified by this executable.
namespace MikuPlayerReplacer
{
    internal sealed class BoneData
    {
        public string Name; public int Parent; public Vector3 LocalPos;
        public Quaternion LocalRot; public bool InheritsRotation, IkControl;
        public int InheritParent = -1; public float InheritRatio;
    }
    internal sealed class MaterialData { public string Name, Texture; public int Cull; }
    internal sealed class MeshData
    {
        public const int RoleBody = 0, RoleHead = 1;
        public string Name; public int Role; public Vector3[] Vertices, Normals;
        public Vector2[] Uvs; public BoneWeight[] Weights;
        public int[] SubMeshMaterials; public int[][] Indices;
    }
    internal sealed class ModelData
    {
        public readonly List<BoneData> Bones = new List<BoneData>();
        public readonly List<MaterialData> Materials = new List<MaterialData>();
        public readonly List<MeshData> Meshes = new List<MeshData>();
    }
    internal static class ModelCompatibilityProbe
    {
        private static string Key(Vector3 v)
        {
            return v.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static void Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            string path = args[0].StartsWith("@") ? File.ReadAllText(args[0].Substring(1)).Trim() : args[0];
            PmxData source = PmxReader.Read(path);
            PmxConverter.Convert(source, "warmup");
            long allocated = 0;
            var allocationMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread");
            if (allocationMethod != null) allocated = (long)allocationMethod.Invoke(null, null);
            Stopwatch watch = Stopwatch.StartNew();
            ModelData model = PmxConverter.Convert(source, "probe");
            watch.Stop();
            if (allocationMethod != null) allocated = (long)allocationMethod.Invoke(null, null) - allocated;
            Console.WriteLine("MODEL " + Path.GetFileName(path) + " bones=" + model.Bones.Count
                + " convert_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F2")
                + " allocated_bytes=" + (allocationMethod != null ? allocated.ToString() : "unavailable"));
            Dictionary<string, BoneWeight> converted = new Dictionary<string, BoneWeight>();
            foreach (MeshData mesh in model.Meshes)
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    BoneWeight w = mesh.Weights[i];
                    float sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
                    if (Math.Abs(sum - 1f) > .0001f) throw new Exception("Non-normalized skin weight");
                    converted[Key(mesh.Vertices[i])] = w;
                }
            Dictionary<string, int> sleeveTargets = new Dictionary<string, int>();
            int sleeves = 0;
            for (int i = 0; i < source.Positions.Length; i++)
            {
                bool sleeve = false;
                for (int k = 0; k < 4; k++)
                {
                    int bone = source.VertexBones[i][k];
                    if (source.VertexWeights[i][k] > .5f && bone >= 0
                        && source.Bones[bone].Name.IndexOf("袖", StringComparison.Ordinal) >= 0) sleeve = true;
                }
                if (!sleeve) continue;
                Vector3 v = source.Positions[i];
                string key = Key(new Vector3(-v.x * PmxConverter.UnitScale, v.y * PmxConverter.UnitScale, -v.z * PmxConverter.UnitScale));
                BoneWeight w;
                if (!converted.TryGetValue(key, out w)) continue;
                int[] idx = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                float[] weights = { w.weight0, w.weight1, w.weight2, w.weight3 };
                int max = 0;
                for (int k = 1; k < 4; k++) if (weights[k] > weights[max]) max = k;
                string name = model.Bones[idx[max]].Name;
                int count; sleeveTargets.TryGetValue(name, out count); sleeveTargets[name] = count + 1;
                sleeves++;
            }
            Console.WriteLine("SLEEVE vertices=" + sleeves);
            foreach (var pair in sleeveTargets) Console.WriteLine("  " + pair.Key + "=" + pair.Value);
            int validLongHair = 0;
            Vector3[] world = new Vector3[model.Bones.Count];
            List<int>[] children = new List<int>[model.Bones.Count];
            bool[] chain = new bool[model.Bones.Count];
            for (int i = 0; i < model.Bones.Count; i++)
            {
                BoneData b = model.Bones[i];
                world[i] = b.LocalPos + (b.Parent >= 0 ? world[b.Parent] : Vector3.zero);
                chain[i] = !b.IkControl && PmxConverter.IsPhysicsChainName(b.Name);
                children[i] = new List<int>();
            }
            for (int i = 0; i < model.Bones.Count; i++)
            {
                int parent = model.Bones[i].Parent;
                if (chain[i] && parent >= 0 && chain[parent]) children[parent].Add(i);
            }
            for (int i = 0; i < model.Bones.Count; i++)
            {
                int parent = model.Bones[i].Parent;
                if (!chain[i] || (parent >= 0 && chain[parent] && children[parent].Count < 2)
                    || !PmxConverter.IsHairName(model.Bones[i].Name)
                    || PmxConverter.IsLightHairName(model.Bones[i].Name)) continue;
                int current = i, segments = 1;
                float length = 0f, last = 0f;
                bool zero = false;
                while (children[current].Count == 1)
                {
                    int next = children[current][0];
                    last = (world[next] - world[current]).magnitude;
                    zero |= last < .002f;
                    length += last; current = next; segments++;
                }
                length += last;
                if (segments >= 2 && !zero && length >= .3f) validLongHair++;
            }
            Console.WriteLine("VALID_LONG_HAIR_CHAINS=" + validLongHair);
            if (args.Length > 1 && args[1] == "--verify")
            {
                if (Path.GetFileName(path).StartsWith("HCMiku") && validLongHair < 2)
                    throw new Exception("Both HCMiku tails must have nonzero segment chains");
                int wrong;
                if (Path.GetFileName(path) == "White.pmx" && sleeveTargets.TryGetValue("clav_R", out wrong) && wrong > 0)
                    throw new Exception("Sour right cuff still follows shoulder");
                Console.WriteLine("COMPATIBILITY_ASSERTIONS=PASS");
            }
            foreach (BoneData bone in model.Bones)
                if (PmxConverter.IsHairName(bone.Name))
                    Console.WriteLine("HAIR " + bone.Name + " parent=" + bone.Parent);
        }
    }
}
