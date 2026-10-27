using System;
using UnityEngine;
namespace MikuPlayerReplacer
{
    internal static class ClothCollisionPoseProbe
    {
        private static readonly System.Random random = new System.Random(19025);
        private static float Next(float min, float max) { return min + (float)random.NextDouble() * (max - min); }
        private static Vector3 V(float range) { return new Vector3(Next(-range,range),Next(-range,range),Next(-range,range)); }
        private static float maximum;
        private static void Close(Vector3 actual, Vector3 expected, string message)
        {
            float error = (actual - expected).magnitude;
            maximum = Math.Max(maximum,error);
            if (float.IsNaN(error) || error > .002f) throw new Exception(message + " error=" + error);
        }
        public static void Main()
        {
            if (new ClothCollisionPose().Valid) throw new Exception("Uncaptured pose is valid");
            for (int i=0;i<10000;i++)
            {
                Quaternion q = new Quaternion(Next(-1,1),Next(-1,1),Next(-1,1),Next(-1,1));
                float length = (float)Math.Sqrt(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w);
                q = new Quaternion(q.x/length,q.y/length,q.z/length,q.w/length);
                Quaternion inverse = new Quaternion(-q.x,-q.y,-q.z,q.w);
                Vector3 position = V(100), scale = new Vector3(Next(.5f,2),Next(.5f,2),Next(.5f,2));
                if (i%2==0) scale.x = -scale.x;
                Vector3 x=q*Vector3.right, y=q*Vector3.up, z=q*Vector3.forward;
                Matrix4x4 forward = new Matrix4x4(), backward = new Matrix4x4();
                forward.m00=x.x*scale.x; forward.m10=x.y*scale.x; forward.m20=x.z*scale.x;
                forward.m01=y.x*scale.y; forward.m11=y.y*scale.y; forward.m21=y.z*scale.y;
                forward.m02=z.x*scale.z; forward.m12=z.y*scale.z; forward.m22=z.z*scale.z;
                forward.m03=position.x; forward.m13=position.y; forward.m23=position.z; forward.m33=1;
                backward.m00=x.x/scale.x; backward.m01=x.y/scale.x; backward.m02=x.z/scale.x;
                backward.m10=y.x/scale.y; backward.m11=y.y/scale.y; backward.m12=y.z/scale.y;
                backward.m20=z.x/scale.z; backward.m21=z.y/scale.z; backward.m22=z.z/scale.z;
                backward.m03=-Vector3.Dot(x,position)/scale.x;
                backward.m13=-Vector3.Dot(y,position)/scale.y;
                backward.m23=-Vector3.Dot(z,position)/scale.z; backward.m33=1;
                ClothCollisionPose pose = new ClothCollisionPose(forward,backward,inverse,scale.x);
                Vector3 local = V(2), expected = position + q*Vector3.Scale(local,scale);
                Close(pose.ToWorld(local),expected,"Local to world changed");
                Close(pose.ToLocal(expected),local,"World to local changed");
                Close(pose.DirectionToLocal(q*local),local,"Direction picked up scale");
                Close(pose.Origin,position,"Broad-phase origin changed");
                if(!pose.Valid || pose.Scale!=scale.x) throw new Exception("Scale/validity changed");
                // Consecutive animated poses must replace, rather than retain,
                // the previous frame's transform, including world translation.
                forward.m03+=3; position.x+=3;
                ClothCollisionPose next = new ClothCollisionPose(forward,backward,inverse,scale.x);
                Close(next.ToWorld(local),expected+new Vector3(3,0,0),"Next pose not refreshed");
            }
            Console.WriteLine("CLOTH_POSE_CHECKS=PASS samples=10000 maxPositionError="+maximum);
        }
    }
}
