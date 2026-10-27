using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace MikuPlayerReplacer
{
    internal static class HairBundleProbe
    {
        private static void Check(bool ok,string text) { if(!ok) throw new Exception(text); }
        private static MeshData Fixture(float separation)
        {
            List<Vector3> vertices=new List<Vector3>(); List<int> triangles=new List<int>();
            for(int piece=0;piece<2;piece++)
            {
                int first=vertices.Count;
                for(int y=0;y<6;y++) for(int x=0;x<5;x++)
                    vertices.Add(new Vector3((piece==0?-1:1)*separation+(x-2)*.01f,1.8f-y*.2f,.22f));
                for(int y=0;y<5;y++) for(int x=0;x<4;x++)
                { int a=first+y*5+x; triangles.AddRange(new[]{a,a+5,a+1,a+1,a+5,a+6}); }
            }
            return new MeshData { Vertices=vertices.ToArray(),Indices=new[]{triangles.ToArray()} };
        }
        public static void Main(string[] args)
        {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            MeshData single=Fixture(.025f); bool[] hair=new bool[single.Vertices.Length];
            for(int i=0;i<hair.Length;i++) hair[i]=true;
            LongHairPlan plan=LongHairGeometry.Build(single,hair,new Vector3(0,1.6f,0));
            Check(plan.Roots.Length==1 && plan.VertexCount==60,"Centered ponytail must remain one bundle");
            MeshData twins=Fixture(.3f);
            plan=LongHairGeometry.Build(twins,hair,new Vector3(0,1.6f,0));
            Check(plan.Roots.Length==2,"Separated twin tails must retain two roots");
            MeshData capped=Fixture(.025f);
            List<Vector3> capVertices=new List<Vector3>(capped.Vertices);
            capVertices.AddRange(new[]{new Vector3(0,1.67f,0),new Vector3(.02f,1.67f,0),new Vector3(0,1.69f,.01f)});
            List<int> capFaces=new List<int>(capped.Indices[0]); capFaces.AddRange(new[]{0,60,61,60,61,62});
            capped.Vertices=capVertices.ToArray(); capped.Indices=new[]{capFaces.ToArray()};
            bool[] capMask=new bool[63]; for(int i=0;i<63;i++) capMask[i]=true;
            plan=LongHairGeometry.Build(capped,capMask,new Vector3(0,1.6f,0));
            Check(plan.VertexGroup[60]<0 && plan.VertexGroup[61]<0 && plan.VertexGroup[62]<0,"Connected scalp must stay rigid");
            plan=LongHairGeometry.Build(twins,new bool[60],new Vector3(0,1.6f,0));
            Check(plan.VertexCount==0,"Non-hair geometry entered a bundle");
            Console.WriteLine("SYNTHETIC_BUNDLE_CHECKS=PASS");
            foreach(string path in File.ReadAllLines(args[0]))
            {
                PmxData source=PmxReader.Read(path); ModelData data=PmxConverter.Convert(source,"bundle-probe");
                Vector3[] world=new Vector3[data.Bones.Count]; int head=-1;
                for(int i=0;i<world.Length;i++)
                { BoneData bone=data.Bones[i]; world[i]=bone.LocalPos+(bone.Parent>=0?world[bone.Parent]:Vector3.zero); if(bone.Name=="head_rig") head=i; }
                Check(head>=0,"Missing head");
                if(data.PreserveYybHairLayout) { Console.WriteLine("YYB_REFERENCE_UNCHANGED "+Path.GetFileName(path)); continue; }
                int grouped=0,bundles=0;
                foreach(MeshData mesh in data.Meshes)
                {
                    if(mesh.Role!=MeshData.RoleHead) continue;
                    hair=LongHairGeometry.HairMask(data,mesh,i=>LongHairGeometry.IsHairMaterialName(data.Materials[i].Name));
                    plan=LongHairGeometry.Build(mesh,hair,world[head]);
                    Check(plan.Roots.Length<=16,"Excessive bundle count");
                    for(int v=0;v<plan.VertexGroup.Length;v++)
                        if(plan.VertexGroup[v]>=0) Check(hair[v],"Face/clothing vertex entered long hair");
                    for(int g=0;g<plan.Roots.Length;g++)
                    {
                        int count=0; float low=float.MaxValue,high=float.MinValue;
                        for(int v=0;v<plan.VertexGroup.Length;v++) if(plan.VertexGroup[v]==g)
                        { count++; low=Math.Min(low,mesh.Vertices[v].y); high=Math.Max(high,mesh.Vertices[v].y); }
                        Check(count>=30 && high-low>=.2f,"Bundle cannot build the reference chain");

                    }
                    grouped+=plan.VertexCount; bundles+=plan.Roots.Length;
                }
                Console.WriteLine("BUNDLES "+Path.GetFileName(path)+" groups="+bundles+" vertices="+grouped);
            }
            Console.WriteLine("HAIR_BUNDLE_CHECKS=PASS");
        }
    }
}
