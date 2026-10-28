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
        public int SecondaryMotion = -1;
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
        public bool PreserveYybHairLayout;
        public readonly List<BoneData> Bones = new List<BoneData>();
        public readonly List<MaterialData> Materials = new List<MaterialData>();
        public readonly List<MeshData> Meshes = new List<MeshData>();
    }
    internal static class ModelCompatibilityProbe
    {
        private static void VerifySecondaryClassification()
        {
            PmxData fixture=new PmxData();
            fixture.Bones.Add(new PmxBone { Name="頭", Parent=-1 });
            fixture.Bones.Add(new PmxBone { Name="后发A1", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="bridge_02", Parent=1 });
            fixture.Bones.Add(new PmxBone { Name="untranslated", EnglishName="Hair_R_03", Parent=2 });
            fixture.Bones.Add(new PmxBone { Name="固定", Parent=1 });
            fixture.Bones.Add(new PmxBone { Name="发射器", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="前发1", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="飘带1", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="スカートhair_01", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="袖口1", Parent=0 });
            fixture.Bones.Add(new PmxBone { Name="untranslated_coat", EnglishName="cape_01", Parent=0 });
            int[] kind=PmxConverter.ClassifySecondaryMotion(fixture);
            if(kind[0]!=0 || kind[1]!=PmxConverter.MotionHair || kind[2]!=PmxConverter.MotionHair
                || kind[3]!=PmxConverter.MotionHair || kind[4]!=0 || kind[5]!=0
                || kind[6]!=(PmxConverter.MotionHair|PmxConverter.MotionLightHair)
                || kind[7]!=PmxConverter.MotionAccessory || kind[8]!=PmxConverter.MotionSkirt
                || kind[9]!=0 || kind[10]!=PmxConverter.MotionClothes)
                throw new Exception("Secondary classification regression");
            // A helper marker at the model origin (YYB's センター先) drives rig
            // controls. A few weighted vertices must not make it a cloth chain.
            PmxData control=new PmxData();
            control.Bones.Add(new PmxBone { Name="全ての親", Parent=-1, Position=Vector3.zero });
            control.Bones.Add(new PmxBone { Name="センター先", Parent=0, Position=Vector3.zero });
            control.Bones.Add(new PmxBone { Name="右Y先", Parent=1, Position=new Vector3(-1.04f,10.99f,-2.04f) });
            control.Positions=new[]{new Vector3(0,10,0)};
            control.VertexBones=new[]{new[]{1,-1,-1,-1}};
            control.VertexWeights=new[]{new[]{1f,0f,0f,0f}};
            control.Indices=new[]{0,0,0};
            control.Materials.Add(new PmxMaterial { Name="Shirt", SurfaceCount=3 });
            int[] controlKind=PmxConverter.ClassifySecondaryMotion(control);
            if(controlKind[1]!=0 || controlKind[2]!=0)
                throw new Exception("Root control bone must not be simulated");
            VerifyAnonymousParts();
        }
        private static PmxData AnonymousFixture(string anchor)
        {
            PmxData data=new PmxData();
            data.Bones.Add(new PmxBone { Name=anchor, Parent=-1, Position=new Vector3(0,20,0) });
            for(int i=1;i<=4;i++) data.Bones.Add(new PmxBone { Name="node_"+i, Parent=i-1, Position=new Vector3(0,20-i*3,0) });
            data.Positions=new[]{new Vector3(0,0,0),new Vector3(0,20,0),new Vector3(1,10,0),new Vector3(-1,10,0)};
            data.VertexBones=new[]{new[]{1,-1,-1,-1},new[]{2,-1,-1,-1},new[]{3,-1,-1,-1},new[]{4,-1,-1,-1}};
            data.VertexWeights=new[]{new[]{1f,0f,0f,0f},new[]{1f,0f,0f,0f},new[]{1f,0f,0f,0f},new[]{1f,0f,0f,0f}};
            data.Indices=new[]{0,1,2,0,2,3};
            data.Materials.Add(new PmxMaterial { Name="opaque", SurfaceCount=6 });
            return data;
        }
        private static void VerifyAnonymousParts()
        {
            PmxData data=AnonymousFixture("頭");
            data.Materials[0].Texture="textures/hair_diffuse.png";
            int[] kind=PmxConverter.ClassifySecondaryMotion(data);
            if(kind[0]!=0) throw new Exception("Hair material must not animate the head driver");
            for(int i=1;i<=4;i++) if((kind[i]&PmxConverter.MotionHair)==0) throw new Exception("Anonymous textured hair missed");
            data.Materials[0].Texture=null;
            kind=PmxConverter.ClassifySecondaryMotion(data);
            for(int i=0;i<kind.Length;i++) if(kind[i]!=0) throw new Exception("Unsupported anonymous part must remain static");
            for(int i=1;i<=4;i++) data.RigidBodies.Add(new PmxRigidBody { Name="rb_"+i, Bone=i, Mode=1, HasJoint=true });
            kind=PmxConverter.ClassifySecondaryMotion(data);
            for(int i=1;i<=4;i++) if((kind[i]&PmxConverter.MotionHair)==0) throw new Exception("Anonymous native-physics hair missed");
            data.Bones[0].Name="下半身";
            kind=PmxConverter.ClassifySecondaryMotion(data);
            for(int i=1;i<=4;i++) if((kind[i]&PmxConverter.MotionSkirt)==0) throw new Exception("Anonymous native-physics skirt missed");
        }
        private static string Key(Vector3 v)
        {
            return v.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static void Main(string[] args)
        {
            try { Run(args); }
            catch(Exception error)
            {
                for(Exception e=error;e!=null;e=e.InnerException)
                    Console.WriteLine("CHECK FAILED: "+e.GetType().FullName+" / "+e.Message);
                Environment.ExitCode=1;
            }
        }
        private static void Run(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            VerifySecondaryClassification();
            string path = args[0].StartsWith("@") ? File.ReadAllText(args[0].Substring(1)).Trim() : args[0];
            PmxData source = PmxReader.Read(path);
            Console.WriteLine("PHYSICS bodies="+source.RigidBodies.Count+" complete="+source.PhysicsMetadataAvailable+" error="+source.PhysicsMetadataError);
            if(args.Length>1 && args[1]=="--metadata-fallback")
            {
                byte[] original=File.ReadAllBytes(path); int offset=checked((int)source.PhysicsMetadataOffset);
                foreach(int tailBytes in new[]{0,3,4})
                {
                    byte[] damaged=new byte[offset+tailBytes]; Array.Copy(original,damaged,offset);
                    for(int i=offset;i<damaged.Length;i++) damaged[i]=255;
                    string temporary=Path.Combine(Path.GetTempPath(),"mms-pmx-tail-"+Guid.NewGuid().ToString("N")+".pmx");
                    try
                    {
                        File.WriteAllBytes(temporary,damaged); PmxData fallback=PmxReader.Read(temporary);
                        if(fallback.Bones.Count!=source.Bones.Count || fallback.Positions.Length!=source.Positions.Length
                            || fallback.Indices.Length!=source.Indices.Length || fallback.PhysicsMetadataAvailable)
                            throw new Exception("Optional physics tail fallback changed geometry");
                    }
                    finally { File.Delete(temporary); }
                }
                Console.WriteLine("OPTIONAL_METADATA_FALLBACK=PASS"); return;
            }
            if(args.Length>1 && (args[1]=="--anonymous" || args[1]=="--structure-only"))
            {
                int[] baseline=PmxConverter.ClassifySecondaryMotion(source);
                for(int i=0;i<source.Bones.Count;i++)
                    if(baseline[i]!=0) { source.Bones[i].Name="node_"+i; source.Bones[i].EnglishName=""; }
                for(int i=0;i<source.RigidBodies.Count;i++) { source.RigidBodies[i].Name="rb_"+i; source.RigidBodies[i].EnglishName=""; }
                if(args[1]=="--structure-only")
                    for(int i=0;i<source.Materials.Count;i++) { source.Materials[i].Name="mat_"+i; source.Materials[i].EnglishName=""; source.Materials[i].Texture="tex_"+i; }
                int[] inferred=PmxConverter.ClassifySecondaryMotion(source);
                int expectedHair=0, recoveredHair=0, expectedCloth=0, recoveredCloth=0, added=0;
                for(int i=0;i<baseline.Length;i++)
                {
                    if((baseline[i]&PmxConverter.MotionHair)!=0) { expectedHair++; if((inferred[i]&PmxConverter.MotionHair)!=0) recoveredHair++; }
                    if((baseline[i]&(PmxConverter.MotionSkirt|PmxConverter.MotionClothes))!=0) { expectedCloth++; if((inferred[i]&(PmxConverter.MotionSkirt|PmxConverter.MotionClothes))!=0) recoveredCloth++; }
                    if(baseline[i]==0 && inferred[i]!=0) added++;
                }
                Console.WriteLine("ANONYMOUS hair="+recoveredHair+"/"+expectedHair+" clothes="+recoveredCloth+"/"+expectedCloth+" additional="+added);
                PmxConverter.Convert(source,"anonymous-probe");
                return;
            }
            if (args.Length > 1 && args[1] == "--bones")
            {
                for (int i=0; i<source.Bones.Count; i++)
                {
                    PmxBone b=source.Bones[i];
                    Console.WriteLine(i+"\t"+b.Parent+"\t"+b.Name+"\t"+b.EnglishName);
                }
                return;
            }
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
            int[] sourceKinds=PmxConverter.ClassifySecondaryMotion(source);
            for(int i=0;i<source.Bones.Count;i++)
            {
                string boneName=source.Bones[i].Name ?? "";
                if(boneName.IndexOf("センター先",StringComparison.Ordinal)>=0
                    || boneName.EndsWith("Y先",StringComparison.Ordinal))
                    Console.WriteLine("CONTROL "+boneName+" kind="+sourceKinds[i]);
            }
            HashSet<string> retained=new HashSet<string>();
            int hairCount=0, clothCount=0, accessoryCount=0;
            foreach(BoneData b in model.Bones)
            {
                retained.Add(b.Name);
                if(PmxConverter.IsSecondaryHair(b)) hairCount++;
                if(PmxConverter.IsSecondarySkirt(b) || PmxConverter.IsSecondaryClothes(b)) clothCount++;
                if((PmxConverter.MotionKind(b)&PmxConverter.MotionAccessory)!=0) accessoryCount++;
            }
            for(int i=0;i<source.Bones.Count;i++)
                if(sourceKinds[i]!=0 && !retained.Contains(source.Bones[i].Name))
                    throw new Exception("Secondary bone lost: "+source.Bones[i].Name);
            Console.WriteLine("SECONDARY hair="+hairCount+" cloth="+clothCount+" accessory="+accessoryCount);
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
                chain[i] = !b.IkControl && PmxConverter.MotionKind(b) != 0;
                children[i] = new List<int>();
            }
            for (int i = 0; i < model.Bones.Count; i++)
            {
                int parent = model.Bones[i].Parent;
                if (chain[i] && parent >= 0 && chain[parent]
                    && (PmxConverter.MotionKind(model.Bones[i]) & ~PmxConverter.MotionLightHair)
                        == (PmxConverter.MotionKind(model.Bones[parent]) & ~PmxConverter.MotionLightHair)) children[parent].Add(i);
            }
            for (int i = 0; i < model.Bones.Count; i++)
            {
                int parent = model.Bones[i].Parent;
                if (!chain[i] || (parent >= 0 && chain[parent] && children[parent].Count < 2)
                    || !PmxConverter.IsSecondaryHair(model.Bones[i])
                    || (PmxConverter.MotionKind(model.Bones[i]) & PmxConverter.MotionLightHair) != 0) continue;
                int current = i, segments = 1;
                float length = 0f, last = 0f;
                Vector3 previous=world[i];
                while (children[current].Count == 1)
                {
                    int next = children[current][0];
                    float distance=(world[next]-previous).magnitude;
                    if(distance >= .002f) { last=distance; length+=last; previous=world[next]; segments++; }
                    current=next;
                }
                length += last;
                if (segments >= 2 && length >= .3f) validLongHair++;
            }
            Console.WriteLine("VALID_LONG_HAIR_CHAINS=" + validLongHair);
            if (args.Length > 1 && args[1] == "--verify")
            {
                if (Path.GetFileName(path).StartsWith("HCMiku") && validLongHair < 2)
                    throw new Exception("Both HCMiku tails must have nonzero segment chains");
                if ((Path.GetFileName(path).Contains("Azur Lane") || Path.GetFileName(path).Contains("碧蓝航线"))
                    && (hairCount < 10 || validLongHair == 0))
                    throw new Exception("New model hair rig was not preserved/simulatable");
                int wrong;
                if (Path.GetFileName(path) == "White.pmx" && sleeveTargets.TryGetValue("clav_R", out wrong) && wrong > 0)
                    throw new Exception("Sour right cuff still follows shoulder");
                Console.WriteLine("COMPATIBILITY_ASSERTIONS=PASS");
            }
            foreach (BoneData bone in model.Bones)
                if (PmxConverter.IsSecondaryHair(bone))
                    Console.WriteLine("HAIR " + bone.Name + " parent=" + bone.Parent);
            foreach (BoneData bone in model.Bones)
                if (PmxConverter.IsSecondaryClothes(bone))
                    Console.WriteLine("CLOTHES " + bone.Name + " parent=" + bone.Parent);
        }
    }
}
