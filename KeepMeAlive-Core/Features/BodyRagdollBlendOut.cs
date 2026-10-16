//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Blend Out ]====================
    // Easing the bones back into EFT's animation once the body is given back.
    internal static partial class BodyRagdoll
    {
        //====================[ Blend Out ]====================
        // Blends the downed pose back into EFT's animation after the body is given back.
        [DefaultExecutionOrder(BodyAnimation.LateWriteOrder)]
        private sealed class BlendOut : MonoBehaviour
        {
            private sealed class Entry
            {
                public Transform Bone;
                public Quaternion FromRotation;
                public Vector3 FromPosition;
                public Quaternion ToRotation;
                public Vector3 ToPosition;
                public bool HasWritten;
                public Quaternion WrittenRotation;
                public Vector3 WrittenPosition;
            }

            private readonly List<Entry> _entries = new List<Entry>();
            private float _t;
            private int _frame;

            // The bone to log each frame while debug logs are on (the pelvis), and what drives it.
            public Transform Trace;
            public Func<bool> AnimatorOn;

            // The pose on screen now, which the blend starts from.
            public void Capture(Transform bone)
            {
                _entries.Add(new Entry
                {
                    Bone = bone,
                    FromRotation = bone.localRotation,
                    FromPosition = bone.localPosition,
                    ToRotation = bone.localRotation,
                    ToPosition = bone.localPosition
                });
            }

            // Takes the bones as they stand now (after EFT's animator was evaluated) as the pose the blend settles into,
            // for any bone the animator has not rewritten since the blend wrote it, and puts every bone back to the
            // pose it started from. Not the pose the body had when it went down: the animator has played since.
            public void CaptureTargets()
            {
                foreach (var e in _entries)
                {
                    if (e.Bone == null) continue;
                    e.ToRotation = e.Bone.localRotation;
                    e.ToPosition = e.Bone.localPosition;
                    e.Bone.localRotation = e.FromRotation;
                    e.Bone.localPosition = e.FromPosition;
                }
            }

            private void LateUpdate()
            {
                _t += Time.deltaTime / BlendOutDuration;
                bool done = _t >= 1f;
                // The last frame lands exactly on the target.
                float weight = done ? 0f : 1f - Mathf.SmoothStep(0f, 1f, _t);

                foreach (var e in _entries)
                {
                    var bone = e.Bone;
                    if (bone == null) continue;

                    Quaternion targetRotation = bone.localRotation;
                    bool rotationFallback = e.HasWritten && Quaternion.Angle(targetRotation, e.WrittenRotation) < 0.01f;
                    if (rotationFallback)
                        targetRotation = e.ToRotation;
                    bone.localRotation = Quaternion.Slerp(targetRotation, e.FromRotation, weight);
                    e.WrittenRotation = bone.localRotation;

                    Vector3 targetPosition = bone.localPosition;
                    bool positionFallback = e.HasWritten && (targetPosition - e.WrittenPosition).sqrMagnitude < 1e-8f;
                    if (positionFallback)
                        targetPosition = e.ToPosition;
                    bone.localPosition = Vector3.Lerp(targetPosition, e.FromPosition, weight);
                    e.WrittenPosition = bone.localPosition;

                    e.HasWritten = true;

                    if (bone == Trace && RevivalDebugLog.IsDebugLogsEnabled)
                        Plugin.LogSource.LogInfo($"[BlendTrace] f={_frame} w={weight:F2} fallback rot={rotationFallback} pos={positionFallback} " +
                            $"localPos={bone.localPosition} localEuler={bone.localEulerAngles} worldPos={bone.position} worldEuler={bone.eulerAngles} " +
                            $"root={transform.position}/{transform.eulerAngles} animatorOn={(AnimatorOn != null && AnimatorOn())}");
                }
                _frame++;

                if (done) Destroy(this);
            }
        }
    }
}
