using System;
using System.IO;
using UnityEngine;

namespace MikuPlayerReplacer
{
    internal static class NeckCapProbe
    {
        public static void Main(string[] args)
        {
            foreach (string path in File.ReadAllLines(args[0]))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                ModelData model = PmxConverter.Convert(PmxReader.Read(path), "neck-probe");
                MeshData body = null, head = null;
                foreach (MeshData mesh in model.Meshes)
                {
                    if (mesh.Role == MeshData.RoleBody) body = mesh;
                    if (mesh.Role == MeshData.RoleHead) head = mesh;
                }
                Vector3[] positions = new Vector3[model.Bones.Count];
                Quaternion[] rotations = new Quaternion[model.Bones.Count];
                int neck = -1, skull = -1;
                for (int i = 0; i < positions.Length; i++)
                {
                    BoneData b = model.Bones[i];
                    rotations[i] = b.Parent < 0 ? b.LocalRot : rotations[b.Parent] * b.LocalRot;
                    positions[i] = b.Parent < 0 ? b.LocalPos
                        : positions[b.Parent] + rotations[b.Parent] * b.LocalPos;
                    if (b.Name == "neck") neck = i;
                    if (b.Name == "head_rig") skull = i;
                }
                if (neck < 0 || skull < 0) throw new Exception("Missing neck/head bones");
                MeshData cap = NeckCapGeometry.Build(body, head, positions[neck], positions[skull]);
                if (cap == null) throw new Exception("No closed neck seam: " + path);
                int triangles = 0;
                foreach (int[] group in cap.Indices)
                {
                    triangles += group.Length / 3;
                    foreach (int i in group)
                        if (i < 0 || i >= cap.Vertices.Length) throw new Exception("Invalid cap index");
                    for (int t = 0; t < group.Length; t += 3)
                    {
                        Vector3 a = cap.Vertices[group[t]], b = cap.Vertices[group[t + 1]], c = cap.Vertices[group[t + 2]];
                        if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-16f)
                            throw new Exception("Degenerate cap triangle");
                    }
                }
                foreach (BoneWeight w in cap.Weights)
                    if (Math.Abs(w.weight0 + w.weight1 + w.weight2 + w.weight3 - 1f) > .00001f)
                        throw new Exception("Invalid cap weights");
                Console.WriteLine(Path.GetFileName(path) + ": rings=" + cap.Indices.Length
                    + " vertices=" + cap.Vertices.Length + " triangles=" + triangles + " PASS");
                for (int i = 0; i < cap.SubMeshMaterials.Length; i++)
                    Console.WriteLine("  seam material=" + model.Materials[cap.SubMeshMaterials[i]].Name
                        + " triangles=" + cap.Indices[i].Length / 3);
            }
        }
    }
}
