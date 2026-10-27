using UnityEngine;
namespace MikuPlayerReplacer
{
    // Body bones do not change during the cloth solve. Read Unity transforms
    // once per solve, rather than once per sample, collider and constraint pass.
    internal struct ClothCollisionPose
    {
        internal Matrix4x4 LocalToWorld, WorldToLocal;
        internal Quaternion InverseRotation;
        internal Vector3 Origin;
        internal float Scale;
        internal bool Valid;

        internal ClothCollisionPose(Matrix4x4 localToWorld, Matrix4x4 worldToLocal,
            Quaternion inverseRotation, float scale)
        {
            LocalToWorld = localToWorld; WorldToLocal = worldToLocal;
            InverseRotation = inverseRotation; Scale = scale;
            Origin = new Vector3(localToWorld.m03, localToWorld.m13, localToWorld.m23);
            Valid = true;
        }
        internal static ClothCollisionPose Capture(Transform bone)
        {
            return bone == null ? new ClothCollisionPose() : new ClothCollisionPose(
                bone.localToWorldMatrix, bone.worldToLocalMatrix,
                Quaternion.Inverse(bone.rotation), bone.lossyScale.x);
        }
        internal Vector3 ToLocal(Vector3 point) { return WorldToLocal.MultiplyPoint3x4(point); }
        internal Vector3 ToWorld(Vector3 point) { return LocalToWorld.MultiplyPoint3x4(point); }
        internal Vector3 DirectionToLocal(Vector3 direction) { return InverseRotation * direction; }
    }
}
