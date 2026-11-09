//====================[ Imports ]====================
using System;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedPose ]====================
    // Animates the local player's downed body: the fall (BakedClip.Fall / BakedClip.CrouchFall) as they go
    // down, then the breathing loop (BakedClip.Loop, which starts on the fall's last frame) for as long as they lie
    // there.
    //
    // When the player takes the body back (they crawl off, or a revive completes) it plays the roll onto the front
    // (BakedClip.GetUp, which starts on the loop's first frame and ends in EFT's prone pose), turning and sliding
    // from where it lay onto the player's real root and body yaw so it ends on EFT's prone pose there.
    [DefaultExecutionOrder(BodyAnimation.LateWriteOrder)]
    internal sealed class DownedPose : MonoBehaviour
    {
        //====================[ Tuning ]====================
        // Blend from the pose on screen into the clip (s) after standing: the fall takes over at once.
        private const float FallEntrySeconds = 0.15f;
        // Blend into the loop when starting without a fall (s).
        private const float LoopEntrySeconds = 0.25f;
        // Blend from the pose on screen into the roll when the player gets up (s).
        private const float GetUpEntrySeconds = 0.25f;
        // How quickly the roll's aim follows the player's root (1/s).
        private const float AimFollowRate = 10f;
        // A head less than this far above the pelvis (m) is already down (prone): no fall.
        private const float MinUprightHeadHeight = 0.3f;
        // The pelvis' height above the player's feet, as a share of its standing height, under which the body is
        // crouched and falls from the crouch. Standing is 1 and the crouch the clip starts from is about 0.43.
        private const float CrouchedHeightShare = 0.72f;
        // How far into the roll (share of it) the first person camera starts sliding from the head to the game's own view
        // (under Spine3); it is there when the roll ends, so the hand-back to the game has nothing left to move.
        private const float RollCameraSlideStart = 0.4f;

        //====================[ Instance ]====================
        private BakedClip _fall;
        private bool _playsFall;
        private BakedClip _loop;
        private ClipRig _rig;
        // Character-frame rotations of the rig's bones, filled each frame.
        private Quaternion[] _pose;
        // Local rotations on screen when this started: blended out of over _entrySeconds.
        private Quaternion[] _captured;
        private float _entrySeconds;
        private Vector3 _pelvisFrom;

        private Player _player;
        // The camera follow failed once: it is left alone from then on (the pose carries on).
        private bool _cameraFailed;
        // The game's own first person view this frame (CameraPosition, read before the pose turns Spine3), while rolling.
        private Vector3 _gameViewPosition;
        private Quaternion _gameViewRotation;
        private bool _hasGameView;
        private Transform _pelvis;
        // Where the pelvis was at the clips' reference frame (standing, before the fall), and the ground under it.
        private Vector3 _pelvisStart;
        private Quaternion _facing;
        // Bends the pose to the real ground (the body onto the slope under it, hands, feet and head onto the ground).
        private GroundFollow _ground;
        // Seconds into the clips (the fall, then the loop), and since this started (for the blend in).
        private float _elapsed;
        private float _sinceStart;

        // Getting up: the roll and where it turns and slides the body to (null otherwise).
        private GetUpState _getUp;

        // The self-revive sequence playing (null otherwise; SelfStimSequence), the hand and pen that go with it, and what the last frame
        // of it asked of them.
        private SelfStimSequence _stim;
        private SelfStimHands _stimHands;
        private float _stimHandFrame = -1f;
        private bool _stimPen;
        // Where the needle is: in from the hold's start, out from the put away's (the voice cries out going in only).
        private enum Needle { Out, In, Withdrawn }
        private Needle _stimNeedle;
        // The sequence's roll put the body down where it is from the player's root: it stays there (the root anchor would slide it).
        private bool _stimPlaced;

        // Playing the roll onto the front, and whether it has finished (the body lies prone on the player).
        public bool IsGettingUp => _getUp != null;

        // The body is still going down (the fall is playing).
        public bool IsPlayingFall => enabled && _playsFall && _fall != null && _getUp == null && _elapsed < _fall.Duration;
        public bool GetUpDone => _getUp != null && _elapsed >= _getUp.Clip.Duration;

        //====================[ Start ]====================
        private enum Opening
        {
            // The fall, then the loop (a body going down).
            Fall,
            // The loop (start of a prone self-stim roll).
            Loop
        }

        // A local player must keep the ordinary EFT body if this install's skeleton cannot drive the baked clips.
        public static bool CanBuildRig(Player player) => ClipRig.TryBuild(player, BakedClip.Fall) != null;

        // The local player's fall: the clip, pelvis and facing recorded as they went down (DownedRagdollController), played
        // from however long ago that was. Null when none was recorded.
        public static DownedPose TryStartLocalFall(Player player, PlayerPoolObject pool)
        {
            var rec = DownedRagdollController.LocalFallOf(player);
            if (rec == null) return null;
            var pose = TryStart(player, pool, Opening.Fall, rec.Clip, rec);
            if (pose != null) pose._elapsed = Mathf.Max(0f, Time.time - rec.Start);
            return pose;
        }

        private static DownedPose TryStart(Player player, PlayerPoolObject pool, Opening opening, BakedClip fall, DownedRagdollController.LocalFall recorded = null)
        {
            var fallClip = fall ?? BakedClip.Fall;
            var loopClip = BakedClip.Loop;
            if (fallClip == null || loopClip == null || !loopClip.SharesRig(fallClip)) return null;

            try
            {
                if (pool == null || pool.RigidbodySpawners == null) return null;
                var rig = ClipRig.TryBuild(player, fallClip);
                if (rig == null) return null;

                var pose = player.gameObject.AddComponent<DownedPose>();
                pose.Setup(player, rig, opening == Opening.Fall ? fall : null, loopClip, LoopEntrySeconds, recorded);
                pose._elapsed = 0f;
                string what = opening == Opening.Loop ? "Resumed" : fall == BakedClip.CrouchFall ? "Falling from a crouch" : "Falling";
                RevivalDebugLog.LogDebug($"[DownedPose] {what} for {player.ProfileId} ({rig.Count} bones, {pose._ground.LimbCount} limbs)");
                return pose;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedPose] Could not start for {player.ProfileId}: {ex.Message}");
                return null;
            }
        }

        private void Setup(Player player, ClipRig rig, BakedClip fall, BakedClip loop, float entrySeconds, DownedRagdollController.LocalFall recorded)
        {
            _player = player;
            _fall = fall;
            _playsFall = fall != null;
            _loop = loop;
            _rig = rig;
            _pose = new Quaternion[rig.Count];
            _captured = new Quaternion[rig.Count];
            for (int k = 0; k < rig.Count; k++) _captured[k] = rig.Bones[k].localRotation;

            _pelvis = rig.Pelvis;
            _pelvisFrom = _pelvis.position;
            _ground = new GroundFollow(player.Transform.position.y);

            if (fall != null)
            {
                _entrySeconds = FallEntrySeconds;
                _facing = recorded != null ? recorded.Facing : Facing(player);
                _pelvisStart = (recorded != null ? recorded.Pelvis : _pelvis.position) - _facing * (fall.Pelvis[0] * rig.Scale);
                _ground.BeginAtRoot(_pelvisStart, _facing * Vector3.forward);
            }
            else
            {
                _entrySeconds = entrySeconds;
                _facing = Facing(player);
                _ground.BeginAtBody(_pelvis.position, _facing * Vector3.forward);
                Vector3 offset = _facing * (loop.Pelvis[0] * rig.Scale);
                _pelvisStart = new Vector3(_pelvis.position.x - offset.x, _ground.StartGroundY + loop.FloorHeight * rig.Scale, _pelvis.position.z - offset.z);
            }

            AddLimb(BoneNames.LeftUpperArm, BoneNames.LeftForearm, BoneNames.LeftPalm);
            AddLimb(BoneNames.RightUpperArm, BoneNames.RightForearm, BoneNames.RightPalm);
            AddLimb(BoneNames.LeftThigh, BoneNames.LeftCalf, BoneNames.LeftFoot);
            AddLimb(BoneNames.RightThigh, BoneNames.RightCalf, BoneNames.RightFoot);
            AddLimb(BoneNames.Neck, null, BoneNames.Head);
        }

        private void AddLimb(string root, string mid, string end)
        {
            Transform r = Find(root), m = mid != null ? Find(mid) : null, e = Find(end);
            if (r == null || e == null || (mid != null && m == null)) return;
            _ground.AddLimb(r, m, e);
        }

        private Transform Find(string name) => BoneNames.Find(_rig.Bones, name);

        //====================[ Getting Up ]====================
        private sealed class GetUpState
        {
            public readonly BakedClip Clip;
            private readonly float _endSpineYaw;
            private readonly Vector3 _endTravel;
            private readonly Quaternion _lieFacing;
            private readonly Vector3 _lieStart;
            private Vector3 _targetRoot;
            private float _targetYaw;
            private float _turn;

            public Vector3 AimRoot { get; private set; }
            private float _aimYaw;
            public float Turn => _turn;

            public GetUpState(BakedClip clip, float endSpineYaw, Vector3 endTravel, Quaternion lieFacing, Vector3 lieStart,
                Vector3 root, float yaw)
            {
                Clip = clip;
                _endSpineYaw = endSpineYaw;
                _endTravel = endTravel;
                _lieFacing = lieFacing;
                _lieStart = lieStart;
                _targetRoot = AimRoot = root;
                _targetYaw = _aimYaw = yaw;
                _turn = Mathf.DeltaAngle(lieFacing.eulerAngles.y, yaw - endSpineYaw);
            }

            public void SetTarget(Vector3 root, float yaw)
            {
                _targetRoot = root;
                _targetYaw = yaw;
            }

            public Vector3 Aim(Vector3 local, float at, out Quaternion facing)
            {
                float follow = global::KeepMeAlive.Animation.Shared.MathUtil.Follow(AimFollowRate);
                AimRoot = Vector3.Lerp(AimRoot, _targetRoot, follow);
                _aimYaw = Mathf.LerpAngle(_aimYaw, _targetYaw, follow);

                float lieYaw = _lieFacing.eulerAngles.y;
                _turn += Mathf.DeltaAngle(lieYaw + _turn, _aimYaw - _endSpineYaw);
                Quaternion aimFacing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(lieYaw + _turn);
                Vector3 lie = _lieStart + _lieFacing * local;
                Vector3 aim = AimRoot + aimFacing * (local - _endTravel);
                float w = Mathf.SmoothStep(0f, 1f, at / Clip.Duration);

                facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(lieYaw + _turn * w);
                return Vector3.Lerp(lie, aim, w);
            }
        }

        // Plays the roll onto the front from the pose on screen, turning and sliding the body from where it lies
        // onto the player's root and body yaw.
        public bool BeginGetUp(Vector3 root, float yaw)
        {
            var clip = BakedClip.GetUp;
            if (clip == null || !_rig.Plays(clip)) return false;
            float endSpineYaw = clip.SpineYaw(clip.Frames - 1);
            if (float.IsNaN(endSpineYaw)) return false;

            EndSelfStim();

            _getUp = new GetUpState(clip, endSpineYaw, clip.Pelvis[clip.Frames - 1] * _rig.Scale, _facing, _pelvisStart, root, yaw);
            _fall = null;

            for (int k = 0; k < _rig.Count; k++) _captured[k] = _rig.Bones[k].localRotation;
            _pelvisFrom = _pelvis.position;
            _entrySeconds = GetUpEntrySeconds;
            _elapsed = 0f;
            _sinceStart = 0f;
            RevivalDebugLog.LogDebug($"[DownedPose] Getting up ({clip.Duration:F2}s) onto yaw {yaw:F0}, turning {_getUp.Turn:F0}");
            return true;
        }

        public void SetGetUpTarget(Vector3 root, float yaw) => _getUp?.SetTarget(root, yaw);

        private Vector3 AimGetUp(Vector3 local, float at)
        {
            Vector3 pelvis = _getUp.Aim(local, at, out Quaternion facing);
            _ground.RootY = _getUp.AimRoot.y;
            _ground.SurfaceDirection = facing * Quaternion.Inverse(_facing) * _ground.SurfaceDirection;
            _facing = facing;
            return pelvis;
        }

        //====================[ Self Stim ]====================
        private const float SelfStimEntrySeconds = 0.25f;
        private const float SelfStimCutSeconds = 0.3f;

        // Takes a body lying prone and plays a sequence on it that begins with the roll onto its back, placed so the
        // roll ends on the player's root, with its spine along their body yaw.
        public static DownedPose TryStartSelfStimFromProne(Player player, PlayerPoolObject pool, SelfStimSequence sequence, Vector3 root)
        {
            if (sequence == null || !sequence.FromProne) return null;
            var pose = TryStart(player, pool, Opening.Loop, null);
            if (pose == null) return null;
            if (pose.PlaceForProneRoll(root, sequence.Yaw) && pose.BeginSelfStim(sequence)) return pose;
            pose.Stop();
            return null;
        }

        private bool PlaceForProneRoll(Vector3 root, float yaw)
        {
            var clip = BakedClip.GetUp;
            if (clip == null || !_rig.Plays(clip)) return false;
            float endSpineYaw = clip.SpineYaw(clip.Frames - 1);
            if (float.IsNaN(endSpineYaw)) return false;

            Quaternion facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(yaw - endSpineYaw);
            _ground.SurfaceDirection = facing * Quaternion.Inverse(_facing) * _ground.SurfaceDirection;
            _facing = facing;
            Vector3 end = _facing * (clip.Pelvis[clip.Frames - 1] * _rig.Scale);
            _pelvisStart = new Vector3(root.x - end.x, _pelvisStart.y, root.z - end.z);
            _stimPlaced = true;
            return true;
        }

        public bool BeginSelfStim(SelfStimSequence sequence)
        {
            if (sequence == null || _getUp != null || IsPlayingFall) return false;
            var clip = BakedClip.SelfStim;
            if (clip == null || !_rig.Plays(clip) || (sequence.FromProne && !_rig.Plays(BakedClip.GetUp))) return false;

            EndSelfStim();
            _stim = sequence;
            _stimHands = SelfStimHands.TryCreate(_player, Find(BoneNames.RightPalm));
            _stimHandFrame = -1f;
            _stimPen = false;
            _stimNeedle = Needle.Out;
            Recapture(SelfStimEntrySeconds);
            RevivalDebugLog.LogDebug($"[DownedPose] Self-stim for {_player.ProfileId}: hold {sequence.HoldSeconds:F1}s, {(sequence.FromProne ? "from prone" : "on the back")}, " +
                                     $"{sequence.TotalSeconds:F1}s of clips, hands {(_stimHands != null)}");
            return true;
        }

        public void CancelSelfStim(float now) => _stim?.Cancel(now);

        public void EndSelfStim()
        {
            _stim = null;
            _stimNeedle = Needle.Out;
            _stimPen = false;
            _stimHandFrame = -1f;
            _stimHands?.Dispose();
            _stimHands = null;
        }

        private void Recapture(float seconds)
        {
            for (int k = 0; k < _rig.Count; k++) _captured[k] = _rig.Bones[k].localRotation;
            _pelvisFrom = _pelvis.position;
            _entrySeconds = seconds;
            _sinceStart = 0f;
        }

        private bool TryStimClip(out BakedClip clip, out float at)
        {
            bool playing = _stim.Evaluate(Time.time, out clip, out at, out var segment, out float handFrame, out bool pen, out bool jumped);
            if (jumped) Recapture(SelfStimCutSeconds);
            if (!playing)
            {
                RevivalDebugLog.LogDebug($"[DownedPose] Self-stim over for {_player.ProfileId}");
                EndSelfStim();
                return false;
            }
            _stimHandFrame = handFrame;
            _stimPen = pen;

            if (segment == SelfStimSequence.Segment.Hold && _stimNeedle == Needle.Out)
            {
                _stimNeedle = Needle.In;
                DownedVoiceController.OnStimNeedle(_player);
                PlayInjectSound();
            }
            else if (segment == SelfStimSequence.Segment.PutAway && _stimNeedle != Needle.Withdrawn)
            {
                // Pulling the needle out is silent. Only a hold too short to have had its own start (in and out in one
                // frame) still gets the one knock, since it never got one going in.
                if (_stimNeedle == Needle.Out)
                {
                    DownedVoiceController.OnStimNeedle(_player);
                    PlayInjectSound();
                }
                _stimNeedle = Needle.Withdrawn;
            }
            return true;
        }

        //====================[ Root Anchor ]====================
        private void PlayInjectSound()
        {
            var palm = Find(BoneNames.RightPalm);
            StimSounds.PlayInject(palm != null ? palm.position : _pelvis.position);
        }

        private const float AnchorFollowRate = 6f;
        private const float AnchorRootRestSpeed = 0.1f;

        private Vector3 _rootSeen;
        private bool _hasRootSeen;

        private Vector3 AnchorReferenceLocal => _loop.Pelvis[0] * _rig.Scale;

        private void AnchorToRoot()
        {
            if (_getUp != null || (_fall != null && _elapsed < _fall.Duration)) return;
            if (_stim != null || _stimPlaced) return;
            if (_player == null || !RMSession.TryGetPlayerState(_player.ProfileId, out var st) || !st.HasLyingOffset) return;

            _ground.RootY = _player.Position.y;

            Vector3 root = _player.Position;
            float dt = Time.deltaTime;
            bool moving = !_hasRootSeen || (dt > 0f && (root - _rootSeen).magnitude / dt > AnchorRootRestSpeed);
            _rootSeen = root;
            _hasRootSeen = true;
            if (moving) return;

            Vector3 want = root + _facing * new Vector3(st.LyingOffset.x, 0f, st.LyingOffset.y);
            Vector3 have = _pelvisStart + _facing * AnchorReferenceLocal;
            Vector3 gap = want - have;
            gap.y = 0f;
            _pelvisStart += gap * global::KeepMeAlive.Animation.Shared.MathUtil.Follow(AnchorFollowRate);
        }

        //====================[ Playback ]====================
        private void LateUpdate()
        {
            _elapsed += Time.deltaTime;
            _sinceStart += Time.deltaTime;
            CaptureGameView();
            try
            {
                AnchorToRoot();
                Pose(_elapsed);
                _stimHands?.Apply(_stim != null ? _stimHandFrame : -1f, _stim != null && _stimPen, Time.deltaTime);
                FollowCamera(null);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedPose] Playback failed: {ex.Message}");
                enabled = false;
            }
        }

        //====================[ First Person Camera ]====================
        private void OnEnable() => Camera.onPreCull += FollowCamera;
        private void OnDisable() => Camera.onPreCull -= FollowCamera;

        private void CaptureGameView()
        {
            _hasGameView = false;
            if (_getUp == null || _player == null || !_player.IsYourPlayer) return;
            var view = _player.CameraPosition;
            if (view == null) return;
            _gameViewPosition = view.position;
            _gameViewRotation = view.rotation;
            _hasGameView = true;
        }

        private float RollCameraSlide()
        {
            if (_getUp == null || !_hasGameView) return 0f;
            float duration = _getUp.Clip.Duration;
            if (duration <= 0f) return 1f;
            float through = Mathf.Clamp01(_elapsed / duration);
            return Mathf.SmoothStep(0f, 1f, (through - RollCameraSlideStart) / (1f - RollCameraSlideStart));
        }

        private void FollowCamera(Camera camera)
        {
            try
            {
                var player = _player;
                if (_cameraFailed || !PlayerUtils.IsFirstPersonLocal(player)) return;
                if (!ModUtils.TryGetMainCamera(out var main) || (camera != null && camera != main)) return;
                var head = PlayerUtils.GetHead(player);
                if (head == null) return;
                LocalBodyCamera.Place(player, head, main, RollCameraSlide(), _gameViewPosition, _gameViewRotation);
            }
            catch (Exception ex)
            {
                _cameraFailed = true;
                Plugin.LogSource.LogWarning($"[DownedPose] Camera follow failed: {ex.Message}");
            }
        }

        private void Pose(float t)
        {
            BakedClip clip;
            float at;
            if (_stim != null && TryStimClip(out clip, out at))
            {
                // The self-revive sequence: its clip and time, placed like any other.
            }
            else if (_getUp != null)
            {
                clip = _getUp.Clip;
                at = Mathf.Min(t, clip.Duration);
            }
            else if (_fall != null && t < _fall.Duration)
            {
                clip = _fall;
                at = t;
            }
            else
            {
                clip = _loop;
                at = Mathf.Repeat(t - (_fall != null ? _fall.Duration : 0f), _loop.Duration);
            }
            clip.FrameAt(at, out int a, out int b, out float f);
            float fade = FadeIn;

            Vector3 local = Vector3.Lerp(clip.Pelvis[a], clip.Pelvis[b], f) * _rig.Scale;
            Vector3 flat = _getUp != null ? AimGetUp(local, at) : _pelvisStart + _facing * local;
            Quaternion align = _ground.Follow(flat, _facing * Vector3.forward, out float groundY);

            _rig.Evaluate(clip, at, _pose);
            Quaternion turn = align * _facing;
            for (int k = 0; k < _rig.Count; k++)
            {
                var bone = _rig.Bones[k];
                if (_rig.IsDriven(k)) bone.rotation = turn * _pose[k];
                else bone.localRotation = Quaternion.Inverse(_pose[_rig.ParentOf(k)]) * _pose[k];
                if (fade < 1f) bone.localRotation = Quaternion.Slerp(_captured[k], bone.localRotation, fade);
            }

            float height = _pelvisStart.y - _ground.StartGroundY + local.y;
            Vector3 ground;
            Vector3 pelvis;
            if (_ground.HasGround)
            {
                ground = new Vector3(flat.x, groundY, flat.z);
                pelvis = ground + align * new Vector3(0f, height, 0f);
            }
            else
            {
                pelvis = new Vector3(flat.x, _pelvisStart.y + local.y, flat.z);
                ground = pelvis - align * new Vector3(0f, height, 0f);
            }
            _pelvis.position = fade < 1f ? Vector3.Lerp(_pelvisFrom, pelvis, fade) : pelvis;

            if (_ground.HasGround) _ground.FitLimbs(ground, align * Vector3.up, fade);
        }

        private float FadeIn => Mathf.SmoothStep(0f, 1f, _sinceStart / _entrySeconds);

        private void OnDestroy() => _stimHands?.Dispose();

        public void Stop()
        {
            enabled = false;
            Destroy(this);
        }

        //====================[ Body Checks ]====================
        public static bool IsUpright(Player player)
        {
            Transform pelvis = PlayerUtils.GetPelvis(player);
            Transform head = PlayerUtils.GetHead(player);
            return pelvis != null && head != null && head.position.y - pelvis.position.y >= MinUprightHeadHeight;
        }

        public static BakedClip FallFor(Player player)
        {
            var stand = BakedClip.Fall;
            if (stand == null || !IsUpright(player)) return null;

            var crouch = BakedClip.CrouchFall;
            if (crouch == null || !crouch.SharesRig(stand) || !TryGetScale(player, out float scale)) return stand;

            float height = PlayerUtils.GetPelvis(player).position.y - player.Transform.position.y;
            return height < CrouchedHeightShare * stand.FloorHeight * scale ? crouch : stand;
        }

        //====================[ Travel ]====================
        public static Quaternion Facing(Player player) => global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(player.Transform.rotation.eulerAngles.y);

        public static bool TryGetScale(Player player, out float scale) => ClipRig.TryGetScale(player, BakedClip.Fall, out scale);

        public static Vector3 Travel(BakedClip clip, float t, Quaternion facing, float scale)
        {
            clip.FrameAt(t, out int a, out int b, out float f);
            Vector3 offset = (Vector3.Lerp(clip.Pelvis[a], clip.Pelvis[b], f) - clip.Pelvis[0]) * scale;
            offset.y = 0f;
            return facing * offset;
        }
    }
}
