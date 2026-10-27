using System;
using System.Collections.Generic;
using UnityEngine;

namespace MikuPlayerReplacer
{
    // Data-only grouping before the existing YYB eight-segment bundle solver.
    internal sealed class LongHairPlan
    {
        public int[] VertexGroup;
        public Vector3[] Roots;
        public int VertexCount;
    }
    internal static class LongHairGeometry
    {
        public static bool IsHairMaterialName(string name)
        {
            string lower=(name ?? "").ToLowerInvariant();
            foreach(string key in new[]{"hair","髪","髮","发","ヘア","ウィッグ","ツインテ","ponytail"})
                if(lower.IndexOf(key,StringComparison.Ordinal)>=0) return true;
            return false;
        }
        public static bool[] HairMask(ModelData data,MeshData mesh,Func<int,bool> hairMaterial)
        {
            bool[] hair=new bool[mesh.Vertices.Length];
            for(int s=0;s<mesh.Indices.Length;s++)
                if(hairMaterial(mesh.SubMeshMaterials[s]))
                    foreach(int v in mesh.Indices[s]) if(v>=0 && v<hair.Length) hair[v]=true;
            for(int v=0;v<hair.Length;v++)
            {
                BoneWeight w=mesh.Weights[v]; float amount=0f;
                amount+=HairWeight(data,w.boneIndex0,w.weight0); amount+=HairWeight(data,w.boneIndex1,w.weight1);
                amount+=HairWeight(data,w.boneIndex2,w.weight2); amount+=HairWeight(data,w.boneIndex3,w.weight3);
                if(amount>=.65f) hair[v]=true;
            }
            return hair;
        }
        private static float HairWeight(ModelData data,int bone,float weight)
        { return bone>=0 && bone<data.Bones.Count && PmxConverter.IsSecondaryHair(data.Bones[bone])?weight:0f; }
        private static int Root(int[] parent,int i)
        { while(parent[i]!=i) { parent[i]=parent[parent[i]]; i=parent[i]; } return i; }
        private static void Union(int[] parent,int a,int b)
        { a=Root(parent,a); b=Root(parent,b); if(a!=b) parent[b]=a; }
        public static LongHairPlan Build(MeshData mesh,bool[] hair,Vector3 head)
        {
            int count=mesh.Vertices.Length;
            if(hair==null || hair.Length!=count) throw new ArgumentException("Hair mask size");
            int[] parent=new int[count], groups=new int[count];
            for(int i=0;i<count;i++) { parent[i]=i; groups[i]=-1; }
            foreach(int[] triangles in mesh.Indices)
                for(int t=0;t+2<triangles.Length;t+=3)
                {
                    int a=triangles[t],b=triangles[t+1],c=triangles[t+2];
                    if(a<0 || b<0 || c<0 || a>=count || b>=count || c>=count || !hair[a] || !hair[b] || !hair[c]) continue;
                    Union(parent,a,b); Union(parent,a,c);
                }
            Dictionary<int,List<int>> shells=new Dictionary<int,List<int>>();
            for(int i=0;i<count;i++) if(hair[i])
            {
                int key=Root(parent,i); List<int> vertices;
                if(!shells.TryGetValue(key,out vertices)) shells[key]=vertices=new List<int>();
                vertices.Add(i);
            }
            List<List<int>> pieces=new List<List<int>>(); List<Vector3> roots=new List<Vector3>();
            Vector3 scalp=head+new Vector3(0,.07f,0);
            foreach(List<int> vertices in shells.Values)
            {
                if(vertices.Count<30) continue;
                float minY=float.MaxValue,maxY=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
                foreach(int v in vertices)
                { Vector3 p=mesh.Vertices[v]; minY=Math.Min(minY,p.y); maxY=Math.Max(maxY,p.y); minZ=Math.Min(minZ,p.z); maxZ=Math.Max(maxZ,p.z); }
                if(minY>=head.y-.25f || maxY-minY<.2f || maxY-minY<=.45f && maxZ-minZ<=.35f) continue;
                List<int> selected=new List<int>(); float top=float.MinValue;
                foreach(int v in vertices)
                {
                    Vector3 p=mesh.Vertices[v];
                    // Hair caps connected to long back hair remain rigid at the skull.
                    if((p-scalp).sqrMagnitude<.14f*.14f && p.y>head.y-.08f) continue;
                    selected.Add(v); top=Math.Max(top,p.y);
                }
                if(selected.Count<30 || top-minY<.2f) continue;
                Vector3 root=Vector3.zero; int hits=0;
                foreach(int v in selected) if(mesh.Vertices[v].y>=top-.08f) { root+=mesh.Vertices[v]; hits++; }
                if(hits==0) continue;
                pieces.Add(selected); roots.Add(root/hits);
            }
            int[] bundles=new int[pieces.Count]; for(int i=0;i<bundles.Length;i++) bundles[i]=i;
            for(int i=0;i<roots.Count;i++) for(int j=0;j<i;j++)
                if((roots[i]-roots[j]).sqrMagnitude<.13f*.13f) Union(bundles,i,j);
            Dictionary<int,int> groupIds=new Dictionary<int,int>(); List<Vector3> centers=new List<Vector3>(); List<int> weights=new List<int>();
            for(int i=0;i<pieces.Count;i++)
            {
                int key=Root(bundles,i),group;
                if(!groupIds.TryGetValue(key,out group))
                { group=groupIds.Count; groupIds[key]=group; centers.Add(Vector3.zero); weights.Add(0); }
                centers[group]+=roots[i]*pieces[i].Count; weights[group]+=pieces[i].Count;
                foreach(int v in pieces[i]) groups[v]=group;
            }
            for(int i=0;i<centers.Count;i++) centers[i]/=weights[i];
            int total=0; foreach(int group in groups) if(group>=0) total++;
            return new LongHairPlan { VertexGroup=groups, Roots=centers.ToArray(), VertexCount=total };
        }
    }
}
