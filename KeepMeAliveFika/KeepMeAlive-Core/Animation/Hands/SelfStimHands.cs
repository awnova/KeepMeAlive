//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ SelfStimHands ]====================
    // The right hand of a downed player stabbing themselves with a stim pen (SelfStimHandClip): the fingers closing on the pen
    // and the pen itself. DownedPose drives the body (the arm and palm are the clip's own, BakedClip.SelfStim); this adds the
    // two things that clip does not carry. Called by DownedPose straight after it has written the bones, so the palm is final.
    internal sealed class SelfStimHands
    {
        //====================[ Tuning ]====================
        // The fingers go from the pose the game has them in to the clip's over this long (s).
        private const float FingerBlendSeconds = 0.25f;

        private readonly Transform _palm;
        private readonly HandFingers _fingers;
        private readonly StimPen _pen;
        private readonly Quaternion _frameLocal;
        private readonly Quaternion[] _pose;
        // Time the fingers have been driven.
        private float _driven;

        private SelfStimHands(Transform palm, HandFingers fingers, StimPen pen, Quaternion frameLocal, int bones)
        {
            _palm = palm;
            _fingers = fingers;
            _pen = pen;
            _frameLocal = frameLocal;
            _pose = new Quaternion[bones];
        }

        // The hand on a palm bone; null (logged) when the clip, the knuckles or the bind pose are missing. The pen may be missing
        // on its own (no mesh or material): the fingers still close, on nothing.
        public static SelfStimHands TryCreate(Player player, Transform palm)
        {
            var clip = SelfStimHandClip.Instance;
            if (clip == null || palm == null) return null;
            if (!HandFrames.TryReadKnuckleFrame(palm, false, out var frameLocal)) return null;
            var fingers = HandFingers.TryCreate(player, palm, false, clip.Bones, "SelfStim");
            if (fingers == null) return null;
            return new SelfStimHands(palm, fingers, StimPen.Create(palm, frameLocal), frameLocal, clip.Bones.Length);
        }

        // Once per frame after the body is posed. frame: the clip's frame, or negative while the clip has no hand yet (the roll).
        public void Apply(float frame, bool penVisible, float deltaTime)
        {
            var clip = SelfStimHandClip.Instance;
            if (clip != null && frame >= 0f)
            {
                _driven += deltaTime;
                clip.Sample(frame, _pose);
                _fingers.Apply(_palm.rotation * _frameLocal, _pose, Mathf.SmoothStep(0f, 1f, _driven / FingerBlendSeconds));
            }
            if (_pen == null) return;
            _pen.SetVisible(penVisible);
            _pen.Follow();
        }

        public void Dispose() => _pen?.Destroy();
    }
}
