//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Hands.DefibHands
{
    //====================[ DefibHandsDriver ]====================
    // One reviver's defibrillator sequence: the paddles come out, rub, press and are put away (DefibHandsTrack). The
    // rub repeats to fill the hold, so the press ends just as the hold does; the put-away follows it. Started once the
    // reviver's hands are empty (DefibHands stows what they held) and they have crouched; a hold too short for the whole
    // sequence plays it faster.
    // The arms are posed by the mod (ArmPoser), after the player's own late pass: each wrist goes to the track's, the hand
    // is turned as the track's, and the elbow points where the track's elbow is, all placed in the reviver's camera frame
    // with the track's aim on the line of sight, so the sequence plays centred on the view. The game holds its own items in
    // that frame: the camera pivots at the Ribcage (where the track is baked from) and turns with the look, replicated to
    // observers. The same pass runs in first person and for observers, so both see it. The fingers are the grip the
    // paddles are fitted to, set after the pose (HandFingers), as the paddles are placed on the palms. The game's own arm IK
    // is not used: its shoulders, elbow goals and weights come from animator curves that depend on what is in the hands,
    // and on empty hands left the arms stiff.
    internal sealed class DefibHandsDriver
    {
        //====================[ Tuning ]====================
        // Crouched enough to start, the arms grown back to this much of their full size (FullScaleArms), and how long to wait for
        // either at most (s).
        private const float CrouchedPose = 0.05f;
        private const float FullSize = 0.95f;
        private const float MaxSettleSeconds = 1.5f;
        // Game hands <-> track hands (s).
        private const float BlendInSeconds = 0.2f;
        private const float BlendOutSeconds = 0.25f;
        // Less of the hold left than this when the defibrillator is ready: skip the sequence (s).
        private const float MinPlaySeconds = 0.3f;
        // Furthest the wrists may bend to lay the hands as the track has them (degrees); the track is a valid pose of this
        // rig, so this only stops a wrist breaking when the player's proportions differ.
        private const float MaxWristBend = 90f;
        // Maximum arm reach as a share of its full length. Crouching can put a shoulder behind the camera,
        // leaving the track's palm too far away and straightening the arm. Shift both palms together by at most
        // MaxPairShift to preserve the track's spacing while keeping a bend.
        private const float MaxReachShare = 0.90f;
        private const float MaxPairShift = 0.12f;
        // The source action's frame the shock sound starts on, inside the shock (ChargeEnd..ShockEnd).
        private const int ShockSoundFrame = 114;

        private enum Phase { Settling, Playing, PuttingAway, BlendingOut, Done }

        private readonly Player _player;
        private readonly DefibHandsTrack _track;
        private readonly float _holdEnd;
        // The reviver's camera, and the turn that puts the track's aim on its forward axis.
        private readonly Transform _camera;
        private readonly Quaternion _toTrack;
        private readonly Transform[] _palms = new Transform[2];
        // Each hand's frame (DefibHandsTrack) relative to its palm bone, from the knuckles; fixed for a skeleton.
        private readonly Quaternion[] _handFrames = new Quaternion[2];
        // Each arm (left, right).
        private readonly ArmPoser[] _arms = new ArmPoser[2];
        // Each hand's fingers (null: the hand keeps the game's) and the track's finger rotations this frame.
        private readonly HandFingers[] _fingers = new HandFingers[2];
        private readonly Quaternion[][] _fingerPose = new Quaternion[2][];

        private Phase _phase = Phase.Settling;
        private float _phaseStart;
        private float _settleEnd = -1f;
        private float _weight;
        private float _frame;
        private DefibPaddles _paddles;

        // Timeline, set once settled (s): when each part starts, how fast it plays, and the rub cycles.
        private float _pullStart, _chargeStart, _shockStart;
        private float _rate, _chargeRate;
        private int _rubCycles;

        // Sounds (DefibSounds): the charge is timed to end as the shock starts, the shock sound plays on ShockSoundFrame.
        private float _chargeSoundAt, _shockSoundAt;
        private bool _chargePlayed, _shockPlayed;
        private BetterSource _chargeSource;
        private float _chargeSourceEnd;

        // The track's hands this frame.
        private DefibHandsTrack.Hand _left, _right;

        private DefibHandsDriver(Player player, DefibHandsTrack track, float holdSeconds, Transform camera)
        {
            _player = player;
            _track = track;
            _holdEnd = Time.time + holdSeconds;
            _camera = camera;
            _toTrack = Quaternion.Inverse(Quaternion.LookRotation(track.Aim, Vector3.up));
        }

        // Null when the track, the camera or a bone is missing.
        public static DefibHandsDriver Create(Player player, float holdSeconds)
        {
            var track = DefibHandsTrack.Instance;
            var bones = player?.PlayerBones;
            var camera = player?.CameraPosition;
            if (track == null || bones == null || camera == null) return null;

            var driver = new DefibHandsDriver(player, track, holdSeconds, camera);
            var twists = ArmPoser.FindTwists(player);
            for (int hand = 0; hand < 2; hand++)
            {
                // The body's own palms from PlayerBones, never a search by name: every item in the hands carries its own
                // copy of the arm skeleton (same names), and that copy goes back to the pool when the item is put away.
                var palm = hand == 0 ? bones.LeftPalm : bones.RightPalm;
                if (palm == null) return null;
                if (!HandFrames.TryReadKnuckleFrame(palm, hand == 0, out var handFrame)) return null;

                driver._palms[hand] = palm;
                driver._arms[hand] = ArmPoser.Create<ArmPoser>(player, hand == 0, palm, twists, "DefibHands");
                driver._handFrames[hand] = handFrame;

                var names = new string[track.FingerBones.Length];
                for (int i = 0; i < names.Length; i++) names[i] = BoneNames.Prefix + (hand == 0 ? "L" : "R") + track.FingerBones[i];
                driver._fingers[hand] = HandFingers.TryCreate(player, palm, hand == 0, names, "DefibHands");
                driver._fingerPose[hand] = new Quaternion[names.Length];
            }
            return driver;
        }

        public bool IsDone => _phase == Phase.Done;

        //====================[ Control ]====================
        // The hold was cancelled: the hands go back to the game's. A finished hold puts the paddles away by itself.
        public void Cancel()
        {
            if (_phase == Phase.Settling) Enter(Phase.Done);
            else if (_phase == Phase.Playing) Enter(Phase.BlendingOut);
        }

        // Once per frame, straight after the player's late pass. holding: the reviver's hands are still the empty hands the
        // sequence plays on.
        public void Tick(bool holding)
        {
            if (_phase == Phase.Done) return;
            float now = Time.time;
            // The game squashes first-person arms along the view's depth; they must be full size to reach the body.
            FullScaleArms.Want(_player, this, true);

            // Something else took the hands (a weapon drawn, the downed flow): stop at once.
            if (!holding && _phase != Phase.Settling)
            {
                Enter(Phase.Done);
                return;
            }

            switch (_phase)
            {
                case Phase.Settling:
                    if (now >= _holdEnd - MinPlaySeconds)
                    {
                        Enter(Phase.Done);
                        return;
                    }
                    if (!holding) return;
                    if (_settleEnd < 0f) _settleEnd = now + MaxSettleSeconds;
                    bool crouched = _player.MovementContext.PoseLevel <= CrouchedPose || _player.IsInPronePose;
                    bool fullSize = FullScaleArms.Scale(_player) >= FullSize;
                    if ((!crouched || !fullSize) && now < _settleEnd) return;
                    _paddles ??= DefibPaddles.Create(_palms, _handFrames);
                    Schedule(now);
                    Enter(Phase.Playing);
                    break;

                case Phase.Playing:
                    _weight = Mathf.Clamp01((now - _phaseStart) / BlendInSeconds);
                    PlaySounds(now);
                    if (now >= _holdEnd) Enter(Phase.PuttingAway);
                    break;

                case Phase.PuttingAway:
                    if (now - _phaseStart >= _track.Seconds(_track.ShockEnd, _track.LastFrame)) Enter(Phase.BlendingOut);
                    break;

                case Phase.BlendingOut:
                    _weight = Mathf.Min(_weight, 1f - Mathf.Clamp01((now - _phaseStart) / BlendOutSeconds));
                    if (_weight <= 0f)
                    {
                        Enter(Phase.Done);
                        return;
                    }
                    break;
            }

            if (_phase != Phase.BlendingOut) _frame = FrameAt(now);
            _track.Sample(_frame, out _left, out _right);
            _track.SampleFingers(_frame, _fingerPose[0], _fingerPose[1]);
            // The game's pose for this frame is final (Tick runs after the player's late pass): the arms go to the track's,
            // then the fingers on the hands, then the paddles on the palms.
            PoseArms();
            for (int hand = 0; hand < 2; hand++)
                _fingers[hand]?.Apply(_palms[hand].rotation * _handFrames[hand], _fingerPose[hand], _weight);
            _paddles?.SetVisible(_frame >= _track.PaddlesShow && _frame < _track.PaddlesHide && _weight > 0.5f);
            _paddles?.Follow();
        }

        private void Enter(Phase phase)
        {
            HandTrace.Log($"defib {_player.ProfileId}: {_phase} -> {phase} after {Time.time - _phaseStart:F2}s (weight {_weight:F2}, frame {_frame:F0})");
            _phase = phase;
            _phaseStart = Time.time;
            // Cancelled or cut short before the shock: the charge stops with the hands.
            if (phase == Phase.BlendingOut || phase == Phase.Done) StopCharge();
            if (phase == Phase.Done) Dispose();
        }

        public void Dispose()
        {
            StopCharge();
            FullScaleArms.Want(_player, this, false);
            _weight = 0f;
            _paddles?.Destroy();
            _paddles = null;
        }

        //====================[ Timeline ]====================
        // Fits the sequence into what is left of the hold: pull out, n rub cycles and the tail of the charge, then the
        // shock, ending on the hold's end. The rub cycles are stretched or squeezed to fill it exactly (at least one, so a
        // short fill rubs faster); with no room at all, the pull-out, tail and shock play faster instead.
        private void Schedule(float now)
        {
            float pull = _track.Seconds(_track.FirstFrame, _track.RubStart);
            float cycle = _track.Seconds(_track.RubStart, _track.RubEnd);
            float tail = _track.Seconds(_track.RubEnd, _track.ChargeEnd);
            float shock = _track.Seconds(_track.ChargeEnd, _track.ShockEnd);
            float fixedPart = pull + tail + shock;
            float remaining = _holdEnd - now;
            float fill = remaining - fixedPart;

            if (fill > 0f)
            {
                _rate = 1f;
                _rubCycles = Mathf.Max(1, Mathf.RoundToInt(fill / cycle));
                _chargeRate = (_rubCycles * cycle + tail) / (fill + tail);
            }
            else
            {
                _rate = fixedPart / remaining;
                _rubCycles = 0;
                _chargeRate = _rate;
            }

            _pullStart = now;
            _chargeStart = _pullStart + pull / _rate;
            _shockStart = _chargeStart + (_rubCycles * cycle + tail) / _chargeRate;
            // Not before the paddles are out; then it is cut when the shock sounds.
            _chargeSoundAt = Mathf.Max(_pullStart, _shockStart - DefibSounds.ChargeSeconds);
            _shockSoundAt = _shockStart + _track.Seconds(_track.ChargeEnd, ShockSoundFrame) / _rate;
        }

        // Starts each sound once its moment comes; the charge is cut if it would run into the shock.
        private void PlaySounds(float now)
        {
            if (!_chargePlayed && now >= _chargeSoundAt && now < _shockStart)
            {
                _chargePlayed = true;
                _chargeSource = DefibSounds.PlayCharge(SoundPosition());
                _chargeSourceEnd = now + DefibSounds.ChargeSeconds;
            }
            if (!_shockPlayed && now >= _shockSoundAt)
            {
                _shockPlayed = true;
                StopCharge();
                DefibSounds.PlayShock(SoundPosition());
            }
        }

        // Stops the charge if it is still playing. A finished one is left alone: the game hands its source to other sounds.
        private void StopCharge()
        {
            if (_chargeSource != null && Time.time < _chargeSourceEnd - 0.05f) _chargeSource.Stop(0.05f);
            _chargeSource = null;
        }

        // Between the reviver's palms, where the paddles are.
        private Vector3 SoundPosition() => (_palms[0].position + _palms[1].position) * 0.5f;

        // The source action's frame at a moment of the sequence.
        private float FrameAt(float now)
        {
            var t = _track;
            if (_phase == Phase.PuttingAway)
                return Mathf.Min(t.ShockEnd + (now - _phaseStart) * t.Fps, t.LastFrame);
            if (now < _chargeStart)
                return t.FirstFrame + (now - _pullStart) * _rate * t.Fps;
            if (now < _shockStart)
            {
                float cycle = t.Seconds(t.RubStart, t.RubEnd);
                float u = (now - _chargeStart) * _chargeRate;
                float rub = _rubCycles * cycle;
                float seconds = u < rub ? u % cycle : cycle + (u - rub);
                return t.RubStart + seconds * t.Fps;
            }
            return Mathf.Min(t.ChargeEnd + (now - _shockStart) * _rate * t.Fps, t.ShockEnd);
        }

        //====================[ Pose ]====================
        // The track's hands, blended over the game's by the weight. Per arm: the wrist on the track's palm, the elbow toward
        // the track's elbow, the hand turned as the track's (its knuckle frame on the track's axes).
        private void PoseArms()
        {
            if (_weight <= 0f) return;
            Quaternion frame = Frame();
            Vector3 origin = _camera.position;
            Vector3 shift = PairShift(frame, origin);

            for (int hand = 0; hand < 2; hand++)
            {
                var arm = _arms[hand];
                if (arm == null) continue;
                var pose = hand == 0 ? _left : _right;

                Vector3 wrist = origin + frame * pose.Palm + shift;
                wrist = WithinReach(arm, wrist);
                Quaternion locked = Quaternion.LookRotation(frame * pose.Forward, frame * pose.Up) * Quaternion.Inverse(_handFrames[hand]);

                var from = arm.Capture();
                Vector3 shoulderBefore = arm.UpperBone.position;
                arm.ReachWithShoulder(wrist);
                arm.Solve(wrist, ElbowBend(arm.UpperBone.position, wrist, origin + frame * pose.Elbow + shift));
                arm.Finish(locked, MaxWristBend, from, _weight);

                if (HandTrace.On && Time.time >= _nextTrace[hand]) Trace(hand, arm, origin, frame, shoulderBefore, wrist, origin + frame * pose.Elbow + shift);
            }
        }

        // How far to move both palms together so the arm that has the furthest to go keeps its bend: toward that arm's shoulder, so
        // the paddles stay as far apart as the track has them. Zero when both arms reach with room to spare.
        private Vector3 PairShift(Quaternion frame, Vector3 origin)
        {
            Vector3 shift = Vector3.zero;
            float worst = 0f;
            for (int hand = 0; hand < 2; hand++)
            {
                var arm = _arms[hand];
                if (arm == null) continue;
                Vector3 toWrist = origin + frame * (hand == 0 ? _left : _right).Palm - arm.UpperBone.position;
                float excess = toWrist.magnitude - (arm.UpperLength + arm.ForeLength) * MaxReachShare;
                if (excess <= worst) continue;
                worst = excess;
                shift = -toWrist.normalized * Mathf.Min(excess, MaxPairShift);
            }
            return shift;
        }

        // The wrist pulled toward its shoulder until the arm is no more stretched than MaxReachShare (what the pair's shift
        // left over, when it was capped).
        private static Vector3 WithinReach(ArmPoser arm, Vector3 wrist)
        {
            Vector3 shoulder = arm.UpperBone.position;
            Vector3 toWrist = wrist - shoulder;
            float max = (arm.UpperLength + arm.ForeLength) * MaxReachShare;
            return toWrist.sqrMagnitude > max * max ? shoulder + toWrist.normalized * max : wrist;
        }

        private readonly float[] _nextTrace = new float[2];

        // One line per arm every HandTrace.Interval: where the camera is looking, where the shoulder is, where the track
        // wants the wrist and how much of the arm's length that takes (near 100% is a straight arm), and where the wrist
        // and the elbow ended up.
        private void Trace(int hand, ArmPoser arm, Vector3 origin, Quaternion frame, Vector3 shoulder, Vector3 wrist, Vector3 elbowWanted)
        {
            _nextTrace[hand] = Time.time + HandTrace.Interval;
            float length = arm.UpperLength + arm.ForeLength;
            float reach = (wrist - shoulder).magnitude;
            Vector3 look = _camera.forward;
            var ribcage = PlayerUtils.GetRibcage(_player);
            HandTrace.Log($"defib {_player.ProfileId} {(hand == 0 ? "L" : "R")} {_phase} frame {_frame:F0} weight {_weight:F2} | " +
                $"scale {ribcage.localScale.z:F2} cam {HandTrace.V(origin)} ribcage {HandTrace.V(ribcage.position)} look pitch {-Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg:F0}deg | " +
                $"shoulder {HandTrace.V(shoulder)} (from cam {HandTrace.V(Quaternion.Inverse(frame) * (shoulder - origin))}) | " +
                $"wrist wanted {HandTrace.V(wrist)} got {HandTrace.V(arm.HandBone.position)} short {HandTrace.Cm(arm.Miss)} | " +
                $"reach {HandTrace.Cm(reach)} of {HandTrace.Cm(length)} = {reach / length * 100f:F0}% | " +
                $"elbow wanted {HandTrace.V(elbowWanted)} got {HandTrace.V(arm.ForeBone.position)} | " +
                $"fingers {(_fingers[hand] != null)} paddles {(_paddles != null)}");
        }

        // Which way the elbow points: from the shoulder-wrist line toward the track's elbow, which is the bend the Blender
        // pose has (down and a little out, for both arms). Plain down if the elbow is on the line.
        private static Vector3 ElbowBend(Vector3 shoulder, Vector3 wrist, Vector3 elbow)
        {
            Vector3 bend = ArmPoser.BendToward(shoulder, wrist, elbow);
            return bend.sqrMagnitude > 0.5f ? bend : Vector3.ProjectOnPlane(Vector3.down, wrist - shoulder).normalized;
        }

        // The track's frame follows the camera's forward, so camera roll and shake do not tip the hands sideways.
        // MouseLook turns the camera before this pass.
        private Quaternion Frame() => Quaternion.LookRotation(_camera.forward, Vector3.up) * _toTrack;
    }
}
