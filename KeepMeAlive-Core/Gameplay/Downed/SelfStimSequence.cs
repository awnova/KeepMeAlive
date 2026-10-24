//====================[ Imports ]====================
using System;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ SelfStimSequence ]====================
    // The body clips a downed player plays for the length of their self-revive key hold: stabbing themselves with a stim pen.
    //
    //   roll       prone only: the roll onto the front (BakedClip.GetUp) played backwards, onto the back, ending on the breathing
    //              loop's first frame
    //   takeout    BakedClip.SelfStim up to the hold's first frame: the hand goes to the pouch, takes the pen out and jabs
    //   hold       the clip's hold, a loop, for as long as is left; the needle stays in
    //   put away   the rest of the clip: the pen out, back in the pouch, the hand back on the chest, on the loop's first frame
    //
    // The hold is the elastic part: it takes whatever the key hold has left after the other segments, so the put away ends as the
    // hold ends. A hold shorter than the segments together plays them all faster, with no hold. The owner starts it when the key goes
    // down and every observer when the packet comes; each plays it from its own clock, as the other hold animations are.
    internal sealed class SelfStimSequence
    {
        public enum Segment { Roll, Takeout, Hold, PutAway }

        //====================[ Instance ]====================
        public readonly float Start;
        public readonly float HoldSeconds;
        public readonly bool FromProne;
        // The player's body yaw, which the roll onto the back is turned to (it ends on it: the body lay there prone).
        public readonly float Yaw;

        private readonly BakedClip _clip;
        private readonly BakedClip _roll;
        private readonly SelfStimHandClip _hand;
        // Seconds each segment takes here (the clips' own, sped up when the hold is too short for them), and the whole.
        private readonly float _rollSeconds, _takeoutSeconds, _holdSeconds, _putAwaySeconds;
        private readonly float _rate;
        // Where the sequence stops early (seconds from the start; infinity: it does not), and where the put away starts when it was
        // brought forward by a cancel (negative: not).
        private float _stopAt = float.PositiveInfinity;
        private float _putAwayAt = -1f;
        // A cancel moved the sequence to another segment since DownedPose last asked: the pose jumps and has to blend.
        private bool _jumped;

        public float TotalSeconds => _rollSeconds + _takeoutSeconds + _holdSeconds + _putAwaySeconds;

        private SelfStimSequence(float start, float holdSeconds, bool fromProne, float yaw, BakedClip clip, BakedClip roll, SelfStimHandClip hand)
        {
            Start = start;
            HoldSeconds = holdSeconds;
            FromProne = fromProne;
            Yaw = yaw;
            _clip = clip;
            _roll = roll;
            _hand = hand;

            float rollClip = fromProne ? roll.Duration : 0f;
            float takeout = hand.HoldStart / hand.Fps;
            float putAway = (hand.End - hand.HoldEnd) / hand.Fps;
            float fixedSeconds = rollClip + takeout + putAway;
            _rate = holdSeconds >= fixedSeconds ? 1f : fixedSeconds / Mathf.Max(0.1f, holdSeconds);
            _rollSeconds = rollClip / _rate;
            _takeoutSeconds = takeout / _rate;
            _putAwaySeconds = putAway / _rate;
            _holdSeconds = Mathf.Max(0f, holdSeconds - (_rollSeconds + _takeoutSeconds + _putAwaySeconds));
        }

        // The sequence for a key hold of this length; null when a clip is missing, or does not play on the rig of the others.
        public static SelfStimSequence TryCreate(float start, float holdSeconds, bool fromProne, float yaw)
        {
            var clip = BakedClip.SelfStim;
            var roll = BakedClip.GetUp;
            var hand = SelfStimHandClip.Instance;
            var loop = BakedClip.Loop;
            if (clip == null || hand == null || loop == null || !clip.SharesRig(loop)) return null;
            if (Mathf.Abs(hand.Fps - clip.Fps) > 0.01f || hand.Frames != clip.Frames) return null;
            if (fromProne && (roll == null || !roll.SharesRig(loop))) return null;
            return new SelfStimSequence(start, holdSeconds, fromProne, yaw, clip, roll, hand);
        }

        //====================[ Playback ]====================
        // Where the sequence is at `now`: the clip and the time in it, which segment, and the hand clip's frame (negative while the
        // clip has no hand, the roll) with whether the pen is in it. False once it is over. jumped: a cancel just moved it.
        public bool Evaluate(float now, out BakedClip clip, out float at, out Segment segment, out float handFrame, out bool pen, out bool jumped)
        {
            jumped = _jumped;
            _jumped = false;
            clip = null;
            at = 0f;
            segment = Segment.Roll;
            handFrame = -1f;
            pen = false;

            float t = now - Start;
            if (t < 0f) t = 0f;
            if (t >= _stopAt || t >= TotalSeconds) return false;

            float rollEnd = _rollSeconds;
            float takeoutEnd = rollEnd + _takeoutSeconds;
            float holdEnd = takeoutEnd + _holdSeconds;
            float fps = _hand.Fps;

            if (_putAwayAt >= 0f && t >= _putAwayAt)
            {
                // Brought forward by a cancel: from the hold's last pose, at the clip's pace.
                segment = Segment.PutAway;
                float frame = _hand.HoldEnd + (t - _putAwayAt) * _rate * fps;
                if (frame > _hand.End) return false;
                return Put(frame, ref clip, ref at, out handFrame, out pen);
            }
            if (t < rollEnd)
            {
                segment = Segment.Roll;
                clip = _roll;
                at = Mathf.Clamp(_roll.Duration - t * _rate, 0f, _roll.Duration);
                return true;
            }
            if (t < takeoutEnd)
            {
                segment = Segment.Takeout;
                return Put((t - rollEnd) * _rate * fps, ref clip, ref at, out handFrame, out pen);
            }
            if (t < holdEnd)
            {
                segment = Segment.Hold;
                float period = _hand.HoldEnd - _hand.HoldStart;
                float frame = _hand.HoldStart + Mathf.Repeat((t - takeoutEnd) * fps, period);
                return Put(frame, ref clip, ref at, out handFrame, out pen);
            }
            segment = Segment.PutAway;
            return Put(_hand.HoldEnd + (t - holdEnd) * _rate * fps, ref clip, ref at, out handFrame, out pen);
        }

        private bool Put(float frame, ref BakedClip clip, ref float at, out float handFrame, out bool pen)
        {
            clip = _clip;
            at = frame / _hand.Fps;
            handFrame = frame;
            pen = frame >= _hand.PenShown && frame < _hand.PenStow;
            return true;
        }

        //====================[ Cancel ]====================
        // The key was let go. Before the pen is out there is nothing to put away: it ends where it is. With the pen out, the put away
        // starts now, from the hold's last pose (DownedPose blends from the pose on screen). In the roll it finishes the roll first:
        // a body half way onto its back is not turned over again.
        public void Cancel(float now)
        {
            float t = now - Start;
            if (t >= TotalSeconds || !float.IsPositiveInfinity(_stopAt) || _putAwayAt >= 0f) return;
            if (!Evaluate(now, out _, out _, out var segment, out float frame, out _, out _)) return;
            _jumped = false;

            if (segment == Segment.Roll)
            {
                _stopAt = _rollSeconds;
                return;
            }
            if (segment == Segment.PutAway) return;
            if (frame < _hand.PenShown)
            {
                _stopAt = t;
                _jumped = true;
                return;
            }
            _putAwayAt = t;
            _jumped = true;
        }
    }
}
