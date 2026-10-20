//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Animation.Shared
{
    //====================[ MotionRecorder ]====================
    // Keeps the last two poses of a set of bones, so a ragdoll that takes over from an animation can carry on with
    // the motion each bone had.
    internal sealed class MotionRecorder
    {
        // Sanity limits on the speeds handed over, well above anything the clips do, so a frame hitch cannot fling
        // the body.
        private const float MaxHandoffSpeed = 6f;
        private const float MaxHandoffSpin = 4f * Mathf.PI;

        private readonly Transform[] _tracked;
        private readonly Vector3[] _prevPos, _curPos;
        private readonly Quaternion[] _prevRot, _curRot;
        private float _prevTime, _curTime;
        private bool _hasPrev;

        public MotionRecorder(Transform[] tracked)
        {
            _tracked = tracked;
            _prevPos = new Vector3[tracked.Length];
            _curPos = new Vector3[tracked.Length];
            _prevRot = new Quaternion[tracked.Length];
            _curRot = new Quaternion[tracked.Length];
        }

        // Samples the bones; once per frame.
        public void Record()
        {
            for (int i = 0; i < _tracked.Length; i++)
            {
                _prevPos[i] = _curPos[i];
                _prevRot[i] = _curRot[i];
                var t = _tracked[i];
                if (t == null) continue;
                _curPos[i] = t.position;
                _curRot[i] = t.rotation;
            }
            _prevTime = _curTime;
            _curTime = Time.time;
            _hasPrev = _curTime > _prevTime && _prevTime > 0f;
        }

        // How fast the bone was moving over the last frame, for the ragdoll to carry on with.
        public bool TryGetVelocity(Transform bone, out Vector3 velocity, out Vector3 angularVelocity)
        {
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            if (!_hasPrev) return false;
            float dt = _curTime - _prevTime;
            for (int i = 0; i < _tracked.Length; i++)
            {
                if (_tracked[i] != bone) continue;
                velocity = Vector3.ClampMagnitude((_curPos[i] - _prevPos[i]) / dt, MaxHandoffSpeed);
                float degrees = (_curRot[i] * Quaternion.Inverse(_prevRot[i])).ToSignedAngleAxis(out Vector3 axis);
                if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                    angularVelocity = Vector3.ClampMagnitude(axis * (degrees * Mathf.Deg2Rad / dt), MaxHandoffSpin);
                return true;
            }
            return false;
        }
    }
}
