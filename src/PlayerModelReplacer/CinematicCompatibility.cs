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

        private void ApplyCinematicHeadLook()
        {
            if (!CinematicActive() || _headBone == null || _container == null)
                return;
            Camera cam = Camera.main;
            Transform parent = _container.parent;
            if (cam == null || parent == null) return;
            ReleaseCinematicView(cam);
            // An external shot owns its own framing; it must not turn the
            // character to match a camera metres away from the source skull.
            if ((cam.transform.position - _headBone.position).sqrMagnitude > 0.64f) return;
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
            _headBone.rotation = _container.rotation * Quaternion.Inverse(parent.rotation)
                * cam.transform.rotation * _cinematicHeadRest;
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
            if (cam != null && first && _hideHead.Value && _headRenderer != null && _headBone != null)
            {
                // Include the near-plane corners, not just the camera's centre.
                pad = cam.nearClipPlane * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                    * Mathf.Sqrt(1f + cam.aspect * cam.aspect);
                Bounds bounds = _headRenderer.bounds;
                bounds.Expand(pad * 2f);
                hide = (cam.transform.position - _headBone.position).sqrMagnitude
                    < Mathf.Pow(_headHideDistance.Value + pad, 2f)
                    || bounds.Contains(cam.transform.position);
            }
            SetHeadHidden(hide);
            // During a close scripted look, tail cards can sweep through the
            // camera even when normal gameplay allows first-person tails.
            if (cinematic && hide && _hairRenderer != null)
                _hairRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

            bool hideBody = cam != null && first && cinematic && _hideHead.Value
                && CameraTouchesTorso(cam, pad);
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
