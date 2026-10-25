using System;
using System.Collections.Generic;
using UnityEngine;

namespace MikuPlayerReplacer
{
    // Build once from the actual body/head split. No per-frame mesh reads or baking.
    internal static class NeckCapGeometry
    {
        private sealed class Edge
        {
            internal int A, B, Material, Count;
        }

        private static string Key(Vector3 p)
        {
            const double scale = 1000000.0;
            return Math.Round(p.x * scale) + "/" + Math.Round(p.y * scale)
                + "/" + Math.Round(p.z * scale);
        }

        private static bool NearNeck(Vector3 p, Vector3 neck, Vector3 axis, float length)
        {
            Vector3 d = p - neck;
            float along = Vector3.Dot(d, axis);
            return along >= -length * 0.25f && along <= length * 1.25f
                && (d - axis * along).sqrMagnitude < length * length * 2.25f;
        }

        private static bool EnclosesNeckAxis(List<int> ring, Vector3[] vertices, Vector3 neck, Vector3 axis)
        {
            Vector3 right = Vector3.Cross(axis, Math.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 forward = Vector3.Cross(axis, right);
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Vector3 a = vertices[ring[i]] - neck, b = vertices[ring[j]] - neck;
                float ax = Vector3.Dot(a, right), ay = Vector3.Dot(a, forward);
                float bx = Vector3.Dot(b, right), by = Vector3.Dot(b, forward);
                if ((ay > 0f) != (by > 0f) && ax - ay * (bx - ax) / (by - ay) > 0f)
                    inside = !inside;
            }
            return inside;
        }

        internal static MeshData Build(MeshData body, MeshData head, Vector3 neck, Vector3 skull)
        {
            Vector3 axis = skull - neck;
            float length = axis.magnitude;
            if (body == null || head == null || length < 0.0001f) return null;
            axis /= length;
            HashSet<string> shared = new HashSet<string>();
            foreach (Vector3 p in head.Vertices)
                if (NearNeck(p, neck, axis, length)) shared.Add(Key(p));
            Dictionary<string, int> canonical = new Dictionary<string, int>();
            int[] ids = new int[body.Vertices.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = -1;
                Vector3 p = body.Vertices[i];
                if (!NearNeck(p, neck, axis, length)) continue;
                string key = Key(p);
                if (!shared.Contains(key)) continue;
                int id;
                if (!canonical.TryGetValue(key, out id)) canonical.Add(key, id = i);
                ids[i] = id;
            }
            Dictionary<long, Edge> edges = new Dictionary<long, Edge>();
            for (int s = 0; s < body.Indices.Length; s++)
            {
                int[] triangles = body.Indices[s];
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = ids[triangles[t + k]], b = ids[triangles[t + (k + 1) % 3]];
                        if (a < 0 || b < 0 || a == b) continue;
                        int lo = Math.Min(a, b), hi = Math.Max(a, b);
                        long key = ((long)lo << 32) | (uint)hi;
                        Edge e;
                        if (!edges.TryGetValue(key, out e))
                            edges.Add(key, e = new Edge { A = lo, B = hi, Material = body.SubMeshMaterials[s] });
                        e.Count++;
                    }
            }
            Dictionary<int, List<int>> graph = new Dictionary<int, List<int>>();
            Dictionary<int, int> materials = new Dictionary<int, int>();
            foreach (Edge e in edges.Values)
            {
                if (e.Count != 1) continue;
                List<int> a, b;
                if (!graph.TryGetValue(e.A, out a)) graph.Add(e.A, a = new List<int>());
                if (!graph.TryGetValue(e.B, out b)) graph.Add(e.B, b = new List<int>());
                a.Add(e.B); b.Add(e.A);
                materials[e.A] = e.Material;
                materials[e.B] = e.Material;
            }
            HashSet<int> visited = new HashSet<int>();
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<BoneWeight> weights = new List<BoneWeight>();
            List<int[]> groups = new List<int[]>();
            List<int> slots = new List<int>();
            foreach (int start in graph.Keys)
            {
                if (visited.Contains(start)) continue;
                List<int> ring = new List<int>();
                int current = start, previous = -1;
                bool closed = false;
                while (!visited.Contains(current))
                {
                    visited.Add(current);
                    ring.Add(current);
                    List<int> neighbors = graph[current];
                    if (neighbors.Count != 2) break;
                    int next = neighbors[0] == previous ? neighbors[1] : neighbors[0];
                    if (next == start) { closed = true; break; }
                    previous = current; current = next;
                }
                // Hair cards and ribbons can also cross the head/body split.
                // Only a loop enclosing the neck bone is a neck opening.
                if (!closed || ring.Count < 3 || !EnclosesNeckAxis(ring, body.Vertices, neck, axis)) continue;
                Vector3 center = Vector3.zero;
                Vector2 uv = Vector2.zero;
                Dictionary<int, float> boneWeights = new Dictionary<int, float>();
                foreach (int i in ring)
                {
                    center += body.Vertices[i]; uv += body.Uvs[i];
                    BoneWeight w = body.Weights[i];
                    AddWeight(boneWeights, w.boneIndex0, w.weight0);
                    AddWeight(boneWeights, w.boneIndex1, w.weight1);
                    AddWeight(boneWeights, w.boneIndex2, w.weight2);
                    AddWeight(boneWeights, w.boneIndex3, w.weight3);
                }
                center /= ring.Count; uv /= ring.Count;
                List<KeyValuePair<int, float>> ranked = new List<KeyValuePair<int, float>>(boneWeights);
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
                int[] bones = new int[4]; float[] shares = new float[4]; float total = 0f;
                for (int i = 0; i < Math.Min(4, ranked.Count); i++)
                { bones[i] = ranked[i].Key; shares[i] = ranked[i].Value; total += shares[i]; }
                if (total <= 0f) continue;
                BoneWeight centerWeight = new BoneWeight {
                    boneIndex0 = bones[0], boneIndex1 = bones[1], boneIndex2 = bones[2], boneIndex3 = bones[3],
                    weight0 = shares[0] / total, weight1 = shares[1] / total,
                    weight2 = shares[2] / total, weight3 = shares[3] / total };
                List<int> tris = new List<int>();
                // Separate vertices on each side give valid lighting normals;
                // the opaque cap remains visible from either side of the opening.
                for (int side = 0; side < 2; side++)
                {
                    int first = vertices.Count;
                    vertices.Add(center); uvs.Add(uv); weights.Add(centerWeight);
                    foreach (int i in ring)
                    { vertices.Add(body.Vertices[i]); uvs.Add(body.Uvs[i]); weights.Add(body.Weights[i]); }
                    for (int i = 0; i < ring.Count; i++)
                    {
                        int a = first + 1 + i, b = first + 1 + (i + 1) % ring.Count;
                        tris.Add(first); tris.Add(side == 0 ? a : b); tris.Add(side == 0 ? b : a);
                    }
                }
                groups.Add(tris.ToArray()); slots.Add(materials[start]);
            }
            if (groups.Count == 0) return null;
            return new MeshData { Name = "Miku_NeckInterior", Vertices = vertices.ToArray(),
                Uvs = uvs.ToArray(), Weights = weights.ToArray(), Indices = groups.ToArray(),
                SubMeshMaterials = slots.ToArray() };
        }

        private static void AddWeight(Dictionary<int, float> map, int bone, float value)
        {
            if (value <= 0f) return;
            float old; map.TryGetValue(bone, out old); map[bone] = old + value;
        }
    }
}
