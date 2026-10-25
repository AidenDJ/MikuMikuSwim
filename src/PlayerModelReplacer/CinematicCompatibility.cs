using UnityEngine;
using UnityEngine.Rendering;

namespace MikuPlayerReplacer
{
    public sealed partial class MikuPlayerReplacerPlugin
    {
        private Vector3 _camAppliedLocalPos;
        private Transform _cinematicHeadRig;
        private Quaternion _cinematicHeadRest;
        private bool _cinematicBodyHidden;
        private ShadowCastingMode _cinematicBodyShadow;
        private SkinnedMeshRenderer _neckInterior;
        private readonly System.Collections.Generic.List<Material> _neckInteriorMaterials =
            new System.Collections.Generic.List<Material>();

        private void ReleaseNeckInterior()
        {
            _neckInterior = null;
            foreach (Material material in _neckInteriorMaterials)
                if (material != null) Destroy(material);
            _neckInteriorMaterials.Clear();
        }

        private void BuildNeckInterior()
        {
            if (_bodyRenderer == null || _headRenderer == null || _neckBone == null || _headBone == null)
                return;
            MeshData body = null, head = null;
            foreach (MeshData md in _data.Meshes)
            {
                if (md.Role == MeshData.RoleBody && body == null) body = md;
                if (md.Role == MeshData.RoleHead && head == null) head = md;
            }
            if (body == null || head == null) return;
            Transform space = _bodyRenderer.transform;
            MeshData cap = NeckCapGeometry.Build(body, head,
                space.InverseTransformPoint(_neckBone.position), space.InverseTransformPoint(_headBone.position));
            if (cap == null)
            {
                Logger.LogInfo("Neck interior: no closed head/body seam found; no synthetic surface added.");
                return;
            }
            Shader shader = Shader.Find("Standard");
            if (shader == null) return;
            GameObject go = new GameObject("Miku_NeckInterior");
            go.layer = space.gameObject.layer;
            go.transform.SetParent(space, false);
            Mesh mesh = new Mesh();
            mesh.name = cap.Name;
            mesh.vertices = cap.Vertices;
            mesh.uv = cap.Uvs;
            mesh.boneWeights = cap.Weights;
            mesh.bindposes = _bodyRenderer.sharedMesh.bindposes;
            mesh.subMeshCount = cap.Indices.Length;
            Material[] materials = new Material[cap.Indices.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                mesh.SetTriangles(cap.Indices[i], i);
                Material source = _materials[cap.SubMeshMaterials[i]];
                Material material = new Material(shader);
                material.name = "Miku_NeckInterior_" + i;
                material.mainTexture = source.mainTexture;
                material.mainTextureScale = source.mainTextureScale;
                material.mainTextureOffset = source.mainTextureOffset;
                material.color = source.HasProperty("_Color") ? source.color : Color.white;
                material.SetFloat("_Glossiness", 0f);
                material.SetFloat("_SpecularHighlights", 0f);
                // A dedicated opaque material: do not alter the model's alpha
                // textures or let atlas transparency reopen the neck cap.
                _neckInteriorMaterials.Add(material);
                materials[i] = material;
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _neckInterior = go.AddComponent<SkinnedMeshRenderer>();
            _neckInterior.sharedMesh = mesh;
            _neckInterior.bones = _bodyRenderer.bones;
            _neckInterior.rootBone = _bodyRenderer.rootBone;
            _neckInterior.sharedMaterials = materials;
            _neckInterior.updateWhenOffscreen = true;
            _neckInterior.shadowCastingMode = ShadowCastingMode.Off;
            _neckInterior.enabled = false;
            Logger.LogInfo("Neck interior: " + cap.Vertices.Length + " seam vertices, "
                + cap.Indices.Length + " closed rings.");
        }

        // Release only transforms we still own. A scripted camera may already
        // have written a fresh position/rotation before our final late pass.
        private void ReleaseCinematicView(Camera cam)
        {
            Transform t = cam.transform;
            if (_camMoved && _camMovedT == t)
            {
                if ((t.localPosition - _camAppliedLocalPos).sqrMagnitude < 0.000001f)
                    t.localPosition = _camBaseLocalPos;
                if (Quaternion.Angle(t.localRotation, _camAppliedLocalRot) < 0.05f)
                    t.localRotation = _camBaseLocalRot;
            }
            _camMoved = false;
            _viewBlend = 0f;
            _viewApplied = 0;
        }

        private void ApplyFirstPersonHeadLook()
        {
            if (_headBone == null || _container == null)
                return;
            bool cinematic = CinematicActive();
            // Third-person look is handled separately. First person must also
            // follow the camera while idle and while holding an item.
            float weight = cinematic || _viewApplied == 0 ? 1f : 1f - _viewBlend;
            if (weight <= 0.0001f) return;
            Camera cam = Camera.main;
            Transform parent = _container.parent;
            if (cam == null || parent == null) return;
            if (cinematic) ReleaseCinematicView(cam);
            // An external shot owns its own framing; it must not turn the
            // character to match a camera metres away from the source skull.
            if (cinematic && (cam.transform.position - _headBone.position).sqrMagnitude > 0.64f) return;
            if (_cinematicHeadRig != _headBone)
            {
                _cinematicHeadRig = null;
                for (int i = 0; i < _pairs.Length; i++)
                {
                    if (_pairs[i].Target != _headBone) continue;
                    _cinematicHeadRest = _pairs[i].TargetRestRot;
                    _cinematicHeadRig = _headBone;
                    break;
                }
            }
            if (_cinematicHeadRig == null) return;
            // Some sequences animate the camera only, leaving the source skull
            // upright. Replace its look in mesh space, including roll; adding
            // the camera delta to the animated head would double the tilt.
            Quaternion target = _container.rotation * Quaternion.Inverse(parent.rotation)
                * cam.transform.rotation * _cinematicHeadRest;
            _headBone.rotation = Quaternion.Slerp(_headBone.rotation, target, weight);
        }

        // Camera animations can finish after LateUpdate. Check visibility again
        // immediately before the main camera culls; preview cameras are excluded.
        internal static void ProtectFirstPersonCamera(Camera camera)
        {
            MikuPlayerReplacerPlugin p = _instanceBox as MikuPlayerReplacerPlugin;
            if (ReferenceEquals(p, null) || camera == null || camera != Camera.main
                || !p.MasterEnabled || p._container == null || !p._built
                || !p._container.gameObject.activeInHierarchy) return;
            p.UpdateFirstPersonVisibility();
        }

        private bool CameraTouchesTorso(Camera cam, float pad)
        {
            Vector3 sample = cam.transform.position + cam.transform.forward * cam.nearClipPlane;
            for (int i = 0; i < _bodySlices.Count; i++)
            {
                BodySlices s = _bodySlices[i];
                if (s.Bone == null || s.Limb || s.Frames == null || s.Frames.Length == 0)
                    continue;
                string name = s.Bone.name;
                if (name != "neck" && name != "chest" && name != "spine_1" && name != "hips")
                    continue;
                Vector3 p = s.Bone.InverseTransformPoint(sample);
                float margin = pad / Mathf.Max(0.0001f, Mathf.Abs(s.Bone.lossyScale.x));
                if (p.y < s.Y0 - margin || p.y > s.Y0 + s.Step * s.Frames.Length + margin)
                    continue;
                float at = Mathf.Clamp((p.y - s.Y0) / s.Step - 0.5f, 0f, s.Frames.Length - 1f);
                int a = Mathf.FloorToInt(at);
                int b = Mathf.Min(a + 1, s.Frames.Length - 1);
                Vector3 center = Vector3.Lerp(s.Frames[a], s.Frames[b], at - a);
                Vector2 radius = Vector2.Lerp(s.Radii[a], s.Radii[b], at - a);
                float x = (p.x - center.x) / Mathf.Max(0.0001f, radius.x + margin);
                float z = (p.z - center.z) / Mathf.Max(0.0001f, radius.y + margin);
                if (x * x + z * z <= 1f) return true;
            }
            return false;
        }

        private void UpdateFirstPersonVisibility()
        {
            Camera cam = Camera.main;
            bool cinematic = CinematicActive();
            bool first = cinematic || _viewApplied == 0 || _viewBlend <= 0.1f;
            bool hide = false;
            float pad = 0f;
            if (cam != null && first && (_hideHead.Value || cinematic)
                && _headRenderer != null && _headBone != null)
            {
                // Include the near-plane corners, not just the camera's centre.
                pad = cam.nearClipPlane * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                    * Mathf.Sqrt(1f + cam.aspect * cam.aspect);
                Bounds bounds = _headRenderer.bounds;
                bounds.Expand(pad * 2f);
                hide = (cam.transform.position - _headBone.position).sqrMagnitude
                    < Mathf.Pow(_headHideDistance.Value + pad, 2f)
                    || bounds.Contains(cam.transform.position)
                    || bounds.Contains(cam.transform.position + cam.transform.forward * cam.nearClipPlane);
            }
            SetHeadHidden(hide);
            // Keep the visible tails and their simulation during scripted looks.
            // Interior protection belongs to the skull renderer, not the hair.
            if (cinematic && _hairRenderer != null)
            {
                _hairRenderer.enabled = true;
                _hairRenderer.shadowCastingMode = ShadowCastingMode.On;
            }

            bool hideBody = cam != null && first && cinematic && _hideHead.Value
                && CameraTouchesTorso(cam, pad);
            if (_neckInterior != null)
                _neckInterior.enabled = first && hide && !hideBody;
            if (_bodyRenderer == null) { _cinematicBodyHidden = false; return; }
            if (hideBody && !_cinematicBodyHidden)
            {
                _cinematicBodyShadow = _bodyRenderer.shadowCastingMode;
                _cinematicBodyHidden = true;
            }
            if (_cinematicBodyHidden)
            {
                _bodyRenderer.shadowCastingMode = hideBody
                    ? ShadowCastingMode.ShadowsOnly : _cinematicBodyShadow;
                if (!hideBody) _cinematicBodyHidden = false;
            }
        }
    }
}
