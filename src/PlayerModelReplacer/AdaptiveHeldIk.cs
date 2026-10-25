using System;
using System.Collections.Generic;
using UnityEngine;

namespace MikuPlayerReplacer
{
    // Independent solver: the vanilla skeleton and tool are read-only inputs.
    public sealed partial class MikuPlayerReplacerPlugin
    {
        private sealed class AdaptiveHand
        {
            public Transform Hand;
            public Vector3 ModelPoint, SourcePoint;
            public Quaternion ModelFrame, SourceFrame;
            public Quaternion WristRest;
            public Vector3 PoleLocal;
            public bool PoleValid;
            public float Weight;
            public Quaternion UpperCorrection = Quaternion.identity;
            public Quaternion MidCorrection = Quaternion.identity;
            public Quaternion HandCorrection = Quaternion.identity;
            public Quaternion ClavCorrection = Quaternion.identity;
            public Quaternion AimCorrection = Quaternion.identity;
            public bool TwistSupport;
            public float DistributedTwist;
            public Vector3 ElbowPoleLocal;
            public bool ElbowPoleValid;
            public float NextLog;
        }

        private readonly AdaptiveHand[] _adaptiveHands = new AdaptiveHand[2];
        private int _adaptiveFrame = -1;
        private Transform _adaptiveItem;
        private Quaternion _adaptiveTorsoFrame = Quaternion.identity;

        private bool AdaptiveMode()
        {
            return _holdMode != null && string.Equals(_holdMode.Value,
                "VanillaAdaptive", StringComparison.OrdinalIgnoreCase);
        }

        // Finger roots define a rigid palm plane, independent of finger curl.
        private static bool AdaptivePalm(Transform hand, Transform middle,
            Transform index, Transform pinky, out Vector3 point, out Quaternion frame)
        {
            point = Vector3.zero;
            frame = Quaternion.identity;
            if (hand == null || middle == null || index == null || pinky == null)
                return false;
            Vector3 forward = middle.position - hand.position;
            Vector3 normal = Vector3.Cross(pinky.position - index.position, forward);
            if (forward.sqrMagnitude < 1e-8f || normal.sqrMagnitude < 1e-10f)
                return false;
            // Contact sits behind the knuckles, on the rigid palm rather than a phalanx.
            point = hand.InverseTransformPoint(hand.position + forward * 0.85f);
            frame = Quaternion.Inverse(hand.rotation)
                * Quaternion.LookRotation(forward.normalized, normal.normalized);
            return true;
        }

        private void CalibrateAdaptive(int side, Transform[] bones,
            Dictionary<string, int> indices, SourceMap sources)
        {
            _adaptiveHands[side] = null;
            string suffix = side == 0 ? "L" : "R";
            string[] names = { "hand_" + suffix, "hand_" + suffix + "_midl_base",
                "hand_" + suffix + "_point_base", "hand_" + suffix + "_pinky_base" };
            Transform[] model = new Transform[4], source = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                int index;
                SourceBone bone;
                if (!indices.TryGetValue(names[i], out index)
                    || !sources.Bones.TryGetValue(names[i], out bone)) return;
                model[i] = bones[index];
                source[i] = bone.Bone;
            }
            AdaptiveHand state = new AdaptiveHand();
            if (!AdaptivePalm(model[0], model[1], model[2], model[3],
                    out state.ModelPoint, out state.ModelFrame)
                || !AdaptivePalm(source[0], source[1], source[2], source[3],
                    out state.SourcePoint, out state.SourceFrame)) return;
            state.Hand = model[0];
            state.WristRest = model[0].localRotation;
            _adaptiveHands[side] = state;
            Logger.LogInfo("AdaptiveIK calibrated " + suffix + " palm=" + state.ModelPoint.ToString("F4"));
        }

        // Analytical triangle construction, also exercised by runtime self-tests.
        internal static bool AdaptiveTriangle(Vector3 root, Vector3 target,
            Vector3 pole, float a, float b, out Vector3 elbow, out Vector3 wrist)
        {
            elbow = wrist = root;
            if (!(a > 1e-5f && b > 1e-5f) || !AdaptiveFinite(target)) return false;
            Vector3 delta = target - root;
            float distance = delta.magnitude;
            Vector3 direction = distance > 1e-6f ? delta / distance : Vector3.forward;
            float epsilon = Mathf.Min(a, b) * 0.001f;
            float d = Mathf.Clamp(distance, Mathf.Abs(a - b) + epsilon, a + b - epsilon);
            pole = Vector3.ProjectOnPlane(pole, direction);
            if (pole.sqrMagnitude < 1e-8f)
                pole = Vector3.ProjectOnPlane(Mathf.Abs(direction.y) < 0.9f
                    ? Vector3.up : Vector3.right, direction);
            float along = (a * a + d * d - b * b) / (2f * d);
            float height = Mathf.Sqrt(Mathf.Max(0f, a * a - along * along));
            elbow = root + direction * along + pole.normalized * height;
            wrist = root + direction * d;
            return AdaptiveFinite(elbow) && AdaptiveFinite(wrist);
        }

        private static bool AdaptiveFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsInfinity(v.x)
                || float.IsNaN(v.y) || float.IsInfinity(v.y)
                || float.IsNaN(v.z) || float.IsInfinity(v.z));
        }

        private void AdaptiveSelfTest()
        {
            int count = 0;
            foreach (float a in new[] { 0.18f, 0.3f, 0.5f })
            foreach (float b in new[] { 0.16f, 0.3f, 0.45f })
            foreach (Vector3 target in new[] { Vector3.zero, new Vector3(0, 0, 0.3f),
                new Vector3(0.2f, -0.2f, 0.2f), new Vector3(0, 0, 4), new Vector3(0, 2, 0) })
            {
                Vector3 elbow, wrist;
                if (!AdaptiveTriangle(Vector3.zero, target, Vector3.forward, a, b, out elbow, out wrist)
                    || Mathf.Abs(elbow.magnitude - a) > 0.0001f
                    || Mathf.Abs(Vector3.Distance(elbow, wrist) - b) > 0.0001f)
                    throw new InvalidOperationException("AdaptiveIK triangle invariant failed");
                if (target.magnitude > Mathf.Abs(a - b) + 0.001f
                    && target.magnitude < a + b - 0.001f
                    && Vector3.Distance(target, wrist) > 0.0001f)
                    throw new InvalidOperationException("AdaptiveIK reachable target failed");
                count++;
            }
            Logger.LogInfo("AdaptiveIK self-test PASS: " + count + " reachable, unreachable, folded and collinear cases");
            int poleCases = 0;
            foreach (int side in new[] { 0, 1 })
            foreach (float pitch in new[] { -90f, -60f, 0f, 60f, 90f, 150f })
            foreach (Vector3 target in new[] { new Vector3(.15f, -.2f, .3f),
                Vector3.down, Vector3.up, Vector3.right, Vector3.forward })
            {
                Quaternion rotation = Quaternion.Euler(pitch, 37f, 19f);
                Vector3 baseline = AnatomicalElbowPole(side, target.normalized, Quaternion.identity);
                Vector3 rotated = AnatomicalElbowPole(side, rotation * target.normalized, rotation);
                if (Vector3.Angle(rotation * baseline, rotated) > .1f
                    || Mathf.Abs(Vector3.Dot(rotated, rotation * target.normalized)) > .001f)
                    throw new InvalidOperationException("AdaptiveIK anatomical pole covariance failed");
                foreach (float turn in new[] { -179f, -120f, -61f, -30f, 0f, 30f, 61f, 179f })
                {
                    Vector3 candidate = Quaternion.AngleAxis(turn, rotation * target.normalized) * rotated;
                    Vector3 constrained = ConstrainElbowPole(candidate, rotated, rotation * target.normalized);
                    if (!AdaptiveFinite(constrained) || Vector3.Dot(constrained, rotated) < .49f
                        || Mathf.Abs(Vector3.Dot(constrained, rotation * target.normalized)) > .001f)
                        throw new InvalidOperationException("AdaptiveIK inverted elbow constraint failed");
                }
                poleCases++;
            }
            Logger.LogInfo("AdaptiveIK elbow-safety self-test PASS: " + poleCases + " pitch/side/direction cases, 480 candidate angles");
        }

        private void UpdateAdaptiveHeldIk()
        {
            if (_adaptiveFrame == Time.frameCount) return;
            _adaptiveFrame = Time.frameCount;
            if (!AdaptiveMode())
            {
                foreach (AdaptiveHand h in _adaptiveHands) if (h != null) h.Weight = 0f;
                return;
            }
            Player player = Player.main;
            bool exclusive = player == null || PdaInUse() || CinematicActive()
                || player.motorMode == Player.MotorMode.Vehicle
                || (player.armsController != null && player.armsController.IsBleederAttached());
            Transform item = HeldItemTransform();
            PlayerTool tool = item != null ? item.GetComponent<PlayerTool>() : null;
            bool changed = item != _adaptiveItem;
            _adaptiveItem = item;
            bool left = tool != null && (tool.leftHandIKTarget != null
                || tool.useLeftAimTargetOnPlayer || tool.ikAimLeftArm);
            // Snapshot both shoulders BEFORE either arm solver can modify them.
            Transform torsoNeck = ModelTransform("neck"), torsoHips = ModelTransform("hips");
            Transform torsoL = ModelTransform("shoulder_L"), torsoR = ModelTransform("shoulder_R");
            _adaptiveTorsoFrame = _container.rotation;
            if (torsoNeck != null && torsoHips != null && torsoL != null && torsoR != null)
            {
                Vector3 up = torsoNeck.position - torsoHips.position;
                Vector3 right = Vector3.ProjectOnPlane(torsoR.position - torsoL.position, up);
                if (up.sqrMagnitude > 1e-8f && right.sqrMagnitude > 1e-8f)
                    _adaptiveTorsoFrame = Quaternion.LookRotation(Vector3.Cross(right, up).normalized, up.normalized);
            }
            // No item-name latch: original targets may be released during reload.
            for (int side = 0; side < 2; side++)
            {
                AdaptiveHand state = _adaptiveHands[side];
                if (state == null || state.Hand == null) continue;
                if (exclusive) { state.Weight = 0f; state.PoleValid = state.ElbowPoleValid = false; continue; }
                if (changed) state.PoleValid = state.ElbowPoleValid = false;
                bool active = item != null && (side == 1 || left);
                float duration = tool != null ? Mathf.Clamp(tool.drawTime, 0.08f, 0.5f) : 0.2f;
                state.Weight = Mathf.MoveTowards(state.Weight, active ? 1f : 0f,
                    Time.deltaTime / duration);
                if (state.Weight <= 0f) continue;
                SolveAdaptiveHand(side, state, active);
            }
        }

        private void SolveAdaptiveHand(int side, AdaptiveHand state, bool active)
        {
            string suffix = side == 0 ? "L" : "R";
            Transform upper = ModelTransform("shoulder_" + suffix);
            Transform mid = ModelTransform("elbow_" + suffix);
            Transform hand = state.Hand;
            Transform clav = ModelTransform("clav_" + suffix);
            Transform aimBone = ModelTransform("clav_C_" + suffix);
            Transform source = SourceTransform("hand_" + suffix);
            ArmsController arms = Player.main.armsController;
            PlayerTool tool = _adaptiveItem != null ? _adaptiveItem.GetComponent<PlayerTool>() : null;
            bool rifle = tool is StasisRifle || (tool != null && tool.GetComponent<PropulsionCannon>() != null);
            bool fixedSupport = side == 0 && tool is StasisRifle;
            Transform live = arms != null ? (side == 0 ? arms.leftHand : arms.rightHand) : null;
            // Parent ownership beats the FP preference for hand-parented items.
            if (source != null && _adaptiveItem != null && _adaptiveItem.IsChildOf(source)) live = source;
            // The stasis support grip is authored on the weapon. The FP hand
            // still carries swim-layer rotation, even while that grip is active.
            // FinalIK targets use wrist space, so the same calibrated palm offset applies.
            Transform supportTarget = fixedSupport
                ? (tool.useLeftAimTargetOnPlayer && arms != null
                    ? arms.attachedLeftHandTarget : tool.leftHandIKTarget) : null;
            if (supportTarget != null) live = supportTarget;
            if (live == null) live = source;
            if (upper == null || mid == null || live == null || source == null) return;
            Quaternion uq = upper.localRotation, mq = mid.localRotation, hq = hand.localRotation;
            Quaternion cq = clav != null ? clav.localRotation : Quaternion.identity;
            Quaternion aq = aimBone != null ? aimBone.localRotation : Quaternion.identity;
            bool twoHanded = tool != null && (tool.leftHandIKTarget != null
                || tool.useLeftAimTargetOnPlayer || tool.ikAimLeftArm);
            if (!active)
            {
                // Fade local corrections against THIS frame's animation, never a stale world target.
                if (clav != null) clav.localRotation = cq * Quaternion.Slerp(Quaternion.identity, state.ClavCorrection, state.Weight);
                if (aimBone != null) aimBone.localRotation = aq * Quaternion.Slerp(Quaternion.identity, state.AimCorrection, state.Weight);
                upper.localRotation = uq * Quaternion.Slerp(Quaternion.identity, state.UpperCorrection, state.Weight);
                mid.localRotation = mq * Quaternion.Slerp(Quaternion.identity, state.MidCorrection, state.Weight);
                hand.localRotation = hq * Quaternion.Slerp(Quaternion.identity, state.HandCorrection, state.Weight);
                if (state.TwistSupport)
                    state.DistributedTwist = DistributeAdaptiveTwist(upper, mid, hand, _armRest[side], state.Weight);
                return;
            }
            Vector3 sourceOffset = source.TransformVector(state.SourcePoint);
            Vector3 grip = live.position + live.rotation * Quaternion.Inverse(source.rotation) * sourceOffset;
            Quaternion desired = live.rotation * state.SourceFrame * Quaternion.Inverse(state.ModelFrame);
            Vector3 modelOffset = Quaternion.Inverse(hand.rotation) * hand.TransformVector(state.ModelPoint);
            Vector3 target = grip - desired * modelOffset;
            float a = Vector3.Distance(upper.position, mid.position);
            float b = Vector3.Distance(mid.position, hand.position);
            // Two-handed aim bones carry suit-specific axial roll. Rebuild their
            // anatomical frame from THIS frame's unmodified positions, before IK.
            // Single-hand tools keep the already validated animation path.
            if (twoHanded) PrepareAdaptiveRoot(side, clav, aimBone, upper, mid);
            // Reach assistance rotates the existing clavicle; no bone translation or stretching.
            float excess = Vector3.Distance(upper.position, target) - (a + b) * 0.97f;
            if (!rifle && excess > 0f && clav != null && upper.IsChildOf(clav))
            {
                Vector3 offset = upper.position - clav.position;
                if (offset.sqrMagnitude > 1e-8f)
                {
                    Quaternion assist = Quaternion.FromToRotation(offset, target - clav.position);
                    float maxAngle = Mathf.Min(18f, excess / offset.magnitude * Mathf.Rad2Deg);
                    clav.rotation = Quaternion.RotateTowards(Quaternion.identity, assist, maxAngle) * clav.rotation;
                }
            }
            Vector3 direction = (target - upper.position).normalized;
            Vector3 pole = Vector3.ProjectOnPlane(mid.position - upper.position, direction);
            if (twoHanded)
            {
                Vector3 safe = SafeElbowPole(suffix, direction);
                Transform sourceUpper = SourceTransform("shoulder_" + suffix);
                Transform sourceMid = SourceTransform("elbow_" + suffix);
                if (!fixedSupport && sourceUpper != null && sourceMid != null)
                {
                    Quaternion carry = live.rotation * Quaternion.Inverse(source.rotation);
                    pole = Vector3.ProjectOnPlane(carry * (sourceMid.position - sourceUpper.position), direction);
                }
                // A target can be correct while the elbow is on the back of the
                // arm. Reject the inward/upward half-plane instead of flipping it.
                pole = !fixedSupport && pole.sqrMagnitude > 1e-8f && Vector3.Dot(pole.normalized, safe) > 0.15f
                    ? Vector3.Slerp(safe, pole.normalized, 0.65f) : safe;
                if (state.PoleValid)
                {
                    Vector3 previous = Vector3.ProjectOnPlane(_container.TransformDirection(state.PoleLocal), direction);
                    if (previous.sqrMagnitude > 1e-8f)
                        pole = Vector3.RotateTowards(previous.normalized, pole.normalized,
                            8f * Time.deltaTime, 0f);
                }
            }
            if (pole.sqrMagnitude < 0.0001f && state.PoleValid)
                pole = Vector3.ProjectOnPlane(_container.TransformDirection(state.PoleLocal), direction);
            if (pole.sqrMagnitude < 1e-8f) pole = SafeElbowPole(suffix, direction);
            state.PoleLocal = _container.InverseTransformDirection(pole.normalized);
            state.PoleValid = true;
            ArmRest armRest = _armRest[side];
            if (fixedSupport && supportTarget != null && armRest != null && armRest.HasGripFrame)
            {
                // Select the elbow plane that best matches the fixed support
                // palm's natural forearm direction. A torso-fixed pole can force
                // a 60-degree wrist error during a swim stroke; clamping that
                // error by rotating the palm is the visible downward flip.
                Vector3 naturalElbow = target - desired * armRest.ForearmDirLocal * b;
                Vector3 gripPole = Vector3.ProjectOnPlane(naturalElbow - upper.position, direction);
                if (gripPole.sqrMagnitude > 1e-6f) pole = gripPole.normalized;
            }
            // Apply AFTER every candidate, including the palm-derived left pole.
            // Shared safety for EVERY held object, including fish and tools without PlayerTool.
            // Hand-specific candidates may guide the solve but cannot bypass this constraint.
            {
                Vector3 safe = AnatomicalElbowPole(side, direction, _adaptiveTorsoFrame);
                pole = ConstrainElbowPole(rifle && !fixedSupport ? safe : pole, safe, direction);
                if (state.ElbowPoleValid)
                {
                    Vector3 previous = ConstrainElbowPole(_adaptiveTorsoFrame * state.ElbowPoleLocal, safe, direction);
                    pole = Vector3.RotateTowards(previous, pole, 6f * Time.deltaTime, 0f);
                }
                pole = ConstrainElbowPole(pole, safe, direction);
                state.ElbowPoleLocal = Quaternion.Inverse(_adaptiveTorsoFrame) * pole;
                state.ElbowPoleValid = true;
            }
            float rollUsed = 0f;
            // Bounded iterations reconcile wrist limits with the palm-position target.
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Vector3 elbow, wrist;
                target = grip - desired * modelOffset;
                pole = ConstrainElbowPole(pole,
                    AnatomicalElbowPole(side, (target - upper.position).normalized, _adaptiveTorsoFrame),
                    (target - upper.position).normalized);
                if (!AdaptiveTriangle(upper.position, target, pole, a, b, out elbow, out wrist)) return;
                upper.rotation = Quaternion.FromToRotation(mid.position - upper.position,
                    elbow - upper.position) * upper.rotation;
                mid.rotation = Quaternion.FromToRotation(hand.position - mid.position,
                    wrist - mid.position) * mid.rotation;
                if (fixedSupport && supportTarget != null && armRest != null)
                {
                    OrientAdaptiveSupport(mid, hand, desired, armRest.WristLocal);
                    break;
                }
                // Pronation about the forearm changes orientation without moving its endpoint.
                Quaternion neutral = hand.parent.rotation * state.WristRest;
                Vector3 axis = (hand.position - mid.position).normalized;
                Vector3 from = Vector3.ProjectOnPlane(neutral * state.ModelFrame * Vector3.up, axis);
                Vector3 to = Vector3.ProjectOnPlane(desired * state.ModelFrame * Vector3.up, axis);
                if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                {
                    float roll = Mathf.Clamp(Vector3.SignedAngle(from, to, axis), -65f - rollUsed, 65f - rollUsed);
                    rollUsed += roll;
                    mid.rotation = Quaternion.AngleAxis(roll, axis) * mid.rotation;
                }
                neutral = hand.parent.rotation * state.WristRest;
                float limit = _holdIkMaxWrist != null ? Mathf.Clamp(_holdIkMaxWrist.Value, 5f, 75f) : 45f;
                desired = Quaternion.RotateTowards(neutral, desired, limit);
                hand.rotation = desired;
            }
            // Last orientation limit changed the wrist target: close position once more.
            Vector3 finalElbow, finalWrist;
            target = grip - desired * modelOffset;
            pole = ConstrainElbowPole(pole,
                AnatomicalElbowPole(side, (target - upper.position).normalized, _adaptiveTorsoFrame),
                (target - upper.position).normalized);
            if (AdaptiveTriangle(upper.position, target, pole, a, b, out finalElbow, out finalWrist))
            {
                upper.rotation = Quaternion.FromToRotation(mid.position - upper.position,
                    finalElbow - upper.position) * upper.rotation;
                mid.rotation = Quaternion.FromToRotation(hand.position - mid.position,
                    finalWrist - mid.position) * mid.rotation;
                if (fixedSupport && supportTarget != null && armRest != null)
                    OrientAdaptiveSupport(mid, hand, desired, armRest.WristLocal);
                else hand.rotation = desired;
            }
            state.ClavCorrection = clav != null ? Quaternion.Inverse(cq) * clav.localRotation : Quaternion.identity;
            state.AimCorrection = aimBone != null ? Quaternion.Inverse(aq) * aimBone.localRotation : Quaternion.identity;
            state.UpperCorrection = Quaternion.Inverse(uq) * upper.localRotation;
            state.MidCorrection = Quaternion.Inverse(mq) * mid.localRotation;
            state.HandCorrection = Quaternion.Inverse(hq) * hand.localRotation;
            if (clav != null) clav.localRotation = cq * Quaternion.Slerp(Quaternion.identity, state.ClavCorrection, state.Weight);
            if (aimBone != null) aimBone.localRotation = aq * Quaternion.Slerp(Quaternion.identity, state.AimCorrection, state.Weight);
            upper.localRotation = uq * Quaternion.Slerp(Quaternion.identity, state.UpperCorrection, state.Weight);
            mid.localRotation = mq * Quaternion.Slerp(Quaternion.identity, state.MidCorrection, state.Weight);
            hand.localRotation = hq * Quaternion.Slerp(Quaternion.identity, state.HandCorrection, state.Weight);
            state.TwistSupport = rifle;
            state.DistributedTwist = state.TwistSupport
                ? DistributeAdaptiveTwist(upper, mid, hand, armRest, state.Weight) : 0f;
            if (_holdIkLog.Value && Time.unscaledTime >= state.NextLog)
            {
                state.NextLog = Time.unscaledTime + 1f;
                float error = Vector3.Distance(hand.TransformPoint(state.ModelPoint), grip);
                Vector3 actualPole = Vector3.ProjectOnPlane(mid.position - upper.position, hand.position - upper.position).normalized;
                float elbowSafe = Vector3.Dot(actualPole,
                    AnatomicalElbowPole(side, (hand.position - upper.position).normalized, _adaptiveTorsoFrame));
                Logger.LogInfo(string.Format("AdaptiveIK {0} item={1} weight={2:F2} error={3:F4} reach={4:F4}/{5:F4} angle={6:F1} elbow={7:F1} wrist={8:F1} naturalRoot={9} target={10} twist={11:F1} elbowSafe={12:F3}",
                    suffix, _adaptiveItem.name, state.Weight, error,
                    Vector3.Distance(upper.position, target), a + b,
                    Quaternion.Angle(hand.rotation * state.ModelFrame, live.rotation * state.SourceFrame),
                    Vector3.Angle(upper.position - mid.position, hand.position - mid.position),
                    Quaternion.Angle(state.WristRest, hand.localRotation), twoHanded,
                    supportTarget != null ? supportTarget.name : "vanilla-hand", state.DistributedTwist, elbowSafe));
            }
        }

        private static Vector3 AnatomicalElbowPole(int side, Vector3 direction, Quaternion torso)
        {
            Vector3 outward = torso * (side == 0 ? Vector3.left : Vector3.right);
            Vector3 down = torso * Vector3.down;
            Vector3 pole = Vector3.ProjectOnPlane(outward * .7f + down * .7f, direction);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(outward, direction);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(down, direction);
            return pole.normalized;
        }

        private static Vector3 ConstrainElbowPole(Vector3 candidate, Vector3 safe, Vector3 direction)
        {
            candidate = Vector3.ProjectOnPlane(candidate, direction);
            if (candidate.sqrMagnitude < 1e-8f || Vector3.Dot(candidate.normalized, safe) <= 0f)
                return safe;
            // The allowed elbow arc is at most 60 degrees from outward/downward.
            return Vector3.RotateTowards(safe, candidate.normalized, Mathf.PI / 3f, 0f).normalized;
        }

        private static float DistributeAdaptiveTwist(Transform upper, Transform elbow,
            Transform hand, ArmRest rest, float weight)
        {
            if (rest == null || rest.ArmTwist == null || rest.ArmTwist.parent != upper
                || !elbow.IsChildOf(rest.ArmTwist)) return 0f;
            Vector3 axisWorld = elbow.position - rest.ArmTwist.position;
            if (axisWorld.sqrMagnitude < 1e-8f) return 0f;
            axisWorld.Normalize();
            Quaternion elbowWorld = elbow.rotation;
            Quaternion handWorld = hand.rotation;
            Quaternion neutralElbow = upper.rotation * rest.ElbowLocal;
            // Both the delta AND its extraction axis are in WORLD space.
            // Mixing a local delta with a world axis silently extracts the
            // wrong rotation, especially with the left arm and swimming pitch.
            float angle = SignedTwist(elbowWorld * Quaternion.Inverse(neutralElbow), axisWorld);
            angle = Mathf.Clamp(angle, -150f, 150f) * weight;
            Vector3 axisLocal = Quaternion.Inverse(upper.rotation) * axisWorld;
            rest.ArmTwist.localRotation = Quaternion.AngleAxis(angle, axisLocal);
            if (rest.ArmTwistHelpers != null && rest.ArmTwistRatios != null)
            {
                int count = Mathf.Min(rest.ArmTwistHelpers.Length, rest.ArmTwistRatios.Length);
                for (int i = 0; i < count; i++)
                {
                    Transform helper = rest.ArmTwistHelpers[i];
                    if (helper != null && helper.parent == upper)
                        helper.localRotation = Quaternion.AngleAxis(angle * rest.ArmTwistRatios[i], axisLocal);
                }
            }
            // The twist axis passes through the elbow, so its position stays
            // fixed. Restore solved world frames after changing its parent.
            elbow.rotation = elbowWorld;
            hand.rotation = handWorld;
            return angle;
        }

        private static void OrientAdaptiveSupport(Transform forearm, Transform hand,
            Quaternion palmWrist, Quaternion bindWrist)
        {
            Vector3 direction = hand.position - forearm.position;
            Vector3 localAxis = Quaternion.Inverse(forearm.rotation) * direction;
            Quaternion reference = palmWrist * Quaternion.Inverse(bindWrist);
            // Swing the bind-correct forearm toward the solved endpoint. Its
            // axial roll comes from the weapon, never from the swim animation.
            // This rotation preserves the endpoint; palm orientation is a hard
            // support constraint. Unreachable poses retain explicit reach error.
            forearm.rotation = Quaternion.FromToRotation(reference * localAxis, direction) * reference;
            hand.rotation = palmWrist;
        }

        private void PrepareAdaptiveRoot(int side, Transform clav, Transform aim,
            Transform upper, Transform mid)
        {
            ArmRest rest = _armRest[side];
            if (rest == null || _container == null || upper.parent == null) return;
            Quaternion basis = _container.rotation;
            // Snapshot BEFORE any parent writes; never derive a correction from
            // a child already moved by that correction (the old feedback bug).
            Vector3 upperDirection = mid.position - upper.position;
            Vector3 midDirection = _adaptiveHands[side].Hand.position - mid.position;
            Transform child = aim != null ? aim : upper;
            Vector3 clavDirection = clav != null ? child.position - clav.position : Vector3.zero;
            Vector3 aimDirection = aim != null ? upper.position - aim.position : Vector3.zero;
            if (clav != null && clav.parent != null && rest.HasClav)
            {
                Quaternion parentDelta = clav.parent.rotation * Quaternion.Inverse(basis * rest.ClavParentLocal);
                Quaternion reference = parentDelta * basis * rest.ClavLocal;
                Vector3 referenceDirection = parentDelta * basis * rest.ClavDir;
                clav.rotation = clavDirection.sqrMagnitude > 1e-8f
                    ? Quaternion.FromToRotation(referenceDirection, clavDirection) * reference : reference;
                if (aim != null && upper.parent == aim)
                {
                    Quaternion clavDelta = clav.rotation * Quaternion.Inverse(basis * rest.ClavLocal);
                    Quaternion aimReference = clavDelta * basis * rest.ParentLocal;
                    aim.rotation = rest.HasClavC && aimDirection.sqrMagnitude > 1e-8f
                        ? Quaternion.FromToRotation(clavDelta * basis * rest.ClavCDir, aimDirection) * aimReference
                        : aimReference;
                }
            }
            Quaternion parentSwing = upper.parent.rotation * Quaternion.Inverse(basis * rest.ParentLocal);
            Quaternion upperReference = parentSwing * basis * rest.UpperLocal;
            upper.rotation = Quaternion.FromToRotation(parentSwing * basis * rest.UpperDir,
                upperDirection) * upperReference;
            Quaternion upperSwing = upper.rotation * Quaternion.Inverse(basis * rest.UpperLocal);
            mid.rotation = Quaternion.FromToRotation(upperSwing * basis * rest.MidDir,
                midDirection) * (upperSwing * basis * rest.MidLocal);
        }
    }
}
