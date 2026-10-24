//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedPose ]====================
    // Animates a remote player's downed body whenever nothing is pushing it: the fall (BakedClip.Fall) as they go
    // down, then the breathing loop (BakedClip.Loop, which starts on the fall's last frame) for as long as they lie
    // there. Every rotation is the clip's own. BodyRagdoll swaps it for physics only while the body is dragged and
    // brings it back once the body settles. Runs on every observer.
    //
    // When the player takes the body back (they crawl off, or a revive starts) it plays the roll onto the front
    // (BakedClip.GetUp, which starts on the loop's first frame and ends in EFT's prone pose), always as authored. Over
    // it the body turns and slides from where it lay onto the player's real root and body yaw, given each frame, so it
    // ends on EFT's prone pose there. After a drag the two already agree (the owner is turned toward the dragger, the
    // body is pulled head first to them); after a plain fall the body lies head back, so it turns about half round.
    //
    // The clip is bent to the ground in two layers:
    //   body   - the whole pose is turned to face the way the body faces and onto the slope under it (as EFT's prone
    //            alignment does), with the pelvis kept at the clip's height above the ground under it;
    //   limbs  - each hand, foot and the head that the clip has on its (flat) ground is moved by how far the real
    //            ground under it is above or below that flat ground (a raycast), the arm or leg bent to reach it by
    //            two-bone IK and the neck turned for the head. Parts the clip holds off the ground (the hand on the
    //            chest) are left alone. The moves are smoothed over time, so nothing jitters.
    // On flat ground both layers do nothing and the pose is exactly the clip.
    [DefaultExecutionOrder(BodyAnimation.LateWriteOrder)]
    internal sealed class DownedPose : MonoBehaviour
    {
        //====================[ Tuning ]====================
        // Blend from the pose on screen into the clip (s) after standing: the fall takes over at once.
        private const float FallEntrySeconds = 0.15f;
        // Blend from the ragdoll's pose into the settle clip's first frame (s) once the body has settled: the muscles
        // have been pulling it toward that pose, so it is already close.
        private const float SettleEntrySeconds = 0.25f;
        // Blend into the loop for a body found already lying on its back with no ragdoll before it (joined late) (s).
        private const float LateJoinEntrySeconds = 0.6f;
        // Extra blend time (s) for a body lying fully on its side (90 degrees of roll); less for less roll.
        private const float TiltEntrySeconds = 0.5f;
        // Blend from the pose on screen into the roll when the player gets up (s): from the fall or the loop, and from
        // the ragdoll.
        private const float GetUpEntrySeconds = 0.25f;
        private const float GetUpFromRagdollEntrySeconds = 0.5f;
        // How quickly the roll's aim follows the player's root (1/s), so a correction arriving mid-roll eases in
        // instead of jumping.
        private const float AimFollowRate = 10f;
        // A head less than this far above the pelvis (m) is already down (prone, or joined late): no fall.
        private const float MinUprightHeadHeight = 0.3f;
        // The pelvis' height above the player's feet, as a share of its standing height, under which the body is
        // crouched and falls from the crouch. Standing is 1 and the crouch the clip starts from is about 0.43.
        private const float CrouchedHeightShare = 0.72f;
        // How far into the roll (share of it) the first person camera starts sliding from the head to the game's own view
        // (under Spine3); it is there when the roll ends, so the hand-back to the game has nothing left to move.
        private const float RollCameraSlideStart = 0.4f;
        //====================[ Instance ]====================
        // The clip played first (the standing or the crouched fall, or the settle; null when joining on the loop or once
        // getting up) and then the loop; all share the rig.
        private BakedClip _fall;
        // Whether _fall is a fall (a body going down), not the settle clip.
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
        // Where the needle is: in from the hold's start, out from the put away's (the voice cries out at each).
        private enum Needle { Out, In, Withdrawn }
        private Needle _stimNeedle;
        // The sequence's roll put the body down where it is from the player's root: it stays there (the root anchor would slide it).
        private bool _stimPlaced;

        // Ragdoll bones, sampled each frame so the ragdoll can carry on with their motion.
        private MotionRecorder _motion;

        // Playing the roll onto the front, and whether it has finished (the body lies prone on the player).
        public bool IsGettingUp => _getUp != null;

        // The body is still going down (the fall is playing; not the settle clip, not the loop).
        // A pose whose playback failed is switched off and its time no longer runs: it is not still falling.
        public bool IsPlayingFall => enabled && _playsFall && _fall != null && _getUp == null && _elapsed < _fall.Duration;
        public bool GetUpDone => _getUp != null && _elapsed >= _getUp.Clip.Duration;

        //====================[ Start ]====================
        // What a pose starts out playing.
        private enum Opening
        {
            // The fall, then the loop (a body going down).
            Fall,
            // The settle clip, then the loop (a body the ragdoll has come to rest, after a drag).
            Settle,
            // The loop, placed where the body lies (a body found lying on its back, or the start of the roll).
            Loop
        }

        // Plays the fall and then the loop on a body going down, or returns null when there is nothing to play it on
        // (missing bones or clips, body already down). The caller has already turned off everything else that
        // animates the bones.
        public static DownedPose TryStartFall(Player player, PlayerPoolObject pool)
        {
            var fall = FallFor(player);
            return fall != null ? TryStart(player, pool, Opening.Fall, fall) : null;
        }

        // A local player must keep the ordinary EFT body if this install's skeleton cannot drive the baked clips.
        // Check before BodyRagdoll suspends the local animator for a drag, where no animated pose is started first.
        public static bool CanBuildRig(Player player) => ClipRig.TryBuild(player, BakedClip.Fall) != null;

        // The local player's fall: the clip, pelvis and facing recorded as they went down (DownedRagdollController), played
        // from however long ago that was, so it is where the observers' fall is now. Null when none was recorded.
        public static DownedPose TryStartLocalFall(Player player, PlayerPoolObject pool)
        {
            var rec = DownedRagdollController.LocalFallOf(player);
            if (rec == null) return null;
            var pose = TryStart(player, pool, Opening.Fall, rec.Clip, rec);
            if (pose != null) pose._elapsed = Mathf.Max(0f, Time.time - rec.Start);
            return pose;
        }

        //====================[ Recorded Fall ]====================
        // An observer's fall is the owner's: the clip, the pelvis and the yaw as the owner had them going down
        // (BleedingOutPacket), not what this peer sees of the body, which is later by this observer's own delay and has
        // a root yaw Fika never sent. Every peer then plays the same fall from the same place.
        // A record older than this (s) belongs to an earlier downing (or the packet never came): the body is read
        // as it was before.
        private const float RecordValidSeconds = 30f;

        private static bool HasDownRecord(Player player, out RMPlayer st)
        {
            st = null;
            return !player.IsYourPlayer && RMSession.TryGetPlayerState(player.ProfileId, out st)
                   && Time.time - st.DownFallTime < RecordValidSeconds;
        }

        // The fall the owner recorded, while it is still playing (null once it is over, or when they went down lying).
        private static bool TryGetRecordedFall(Player player, out DownedRagdollController.LocalFall rec)
        {
            rec = null;
            if (!HasDownRecord(player, out var st) || st.DownFallKind == 0) return false;
            var clip = st.DownFallKind == 2 ? BakedClip.CrouchFall : BakedClip.Fall;
            if (clip == null || Time.time - st.DownFallTime >= clip.Duration) return false;
            rec = new DownedRagdollController.LocalFall
            {
                PlayerId = player.ProfileId,
                Clip = clip,
                Start = st.DownFallTime,
                Pelvis = st.DownPelvis,
                Facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(st.DownYaw)
            };
            return true;
        }

        // Whether this observer is to play the owner's fall (so a body that already shows prone is not taken as down
        // already lying).
        public static bool HasRecordedFall(Player player) => TryGetRecordedFall(player, out _);

        // The fall of a body going down on an observer: the owner's when there is one, else read from the body.
        public static DownedPose TryStartFallFor(Player player, PlayerPoolObject pool)
        {
            if (!HasDownRecord(player, out _)) return TryStartFall(player, pool);
            if (!TryGetRecordedFall(player, out var rec)) return null;

            var pose = TryStart(player, pool, Opening.Fall, rec.Clip, rec);
            if (pose != null) pose._elapsed = Mathf.Max(0f, Time.time - rec.Start);
            return pose;
        }

        // Plays the loop on a body that is to lie on its back where the one who let go of it said (DropRestPacket), however it
        // lies here (on its front, as this peer's physics left it): blended in from the pose on screen, turned and slid onto
        // that place. Null when there is nothing to play.
        public static DownedPose TryStartLyingAt(Player player, PlayerPoolObject pool, Vector3 pelvis, float facingYaw)
        {
            var pose = TryStart(player, pool, Opening.Loop, null);
            if (pose == null) return null;
            pose.PlaceAt(pelvis, facingYaw);
            return pose;
        }

        // Puts the resting body's pelvis (level) on a place and turns it to a facing; the ground follow keeps the height.
        private void PlaceAt(Vector3 pelvis, float facingYaw)
        {
            Quaternion facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(facingYaw);
            _ground.SurfaceDirection = facing * Quaternion.Inverse(_facing) * _ground.SurfaceDirection;
            _facing = facing;
            Vector3 offset = _facing * AnchorReferenceLocal;
            _pelvisStart = new Vector3(pelvis.x - offset.x, _pelvisStart.y, pelvis.z - offset.z);
        }

        // Plays the settle clip and then the loop on a ragdoll that has come to rest on its back (after a drag), placed
        // where it lies and blended in from the pose on screen; null when the body is not on its back or there is
        // nothing to play.
        public static DownedPose TrySettle(Player player, PlayerPoolObject pool)
        {
            if (!DragGrip.IsOnBack(player)) return null;
            return TryStart(player, pool, Opening.Settle, null);
        }

        // Plays the loop on a body already lying on its back with no ragdoll before it, placed where it lies and blended
        // in from the pose on screen; null when the body is not on its back or there is nothing to play.
        public static DownedPose TryResume(Player player, PlayerPoolObject pool)
        {
            if (!DragGrip.IsOnBack(player)) return null;
            return TryStart(player, pool, Opening.Loop, null);
        }

        // Plays the roll onto the front on a body lying on its back (after the ragdoll of a drag), from where it lies
        // onto the player's root and body yaw; null when the body is not on its back or there is nothing to play.
        public static DownedPose TryStartGetUp(Player player, PlayerPoolObject pool, Vector3 root, float yaw)
        {
            if (!DragGrip.IsOnBack(player)) return null;
            var pose = TryStart(player, pool, Opening.Loop, null);
            if (pose == null) return null;
            if (pose.BeginGetUp(root, yaw, GetUpFromRagdollEntrySeconds)) return pose;
            pose.Stop();
            return null;
        }

        // fall: the fall to play (Opening.Fall only).
        private static DownedPose TryStart(Player player, PlayerPoolObject pool, Opening opening, BakedClip fall, DownedRagdollController.LocalFall recorded = null)
        {
            var fallClip = fall ?? BakedClip.Fall;
            var loopClip = BakedClip.Loop;
            var intro = opening == Opening.Settle ? BakedClip.Settle : null;
            if (fallClip == null || loopClip == null || !loopClip.SharesRig(fallClip)) return null;
            if (opening == Opening.Settle && (intro == null || !intro.SharesRig(fallClip))) return null;

            try
            {
                if (pool == null || pool.RigidbodySpawners == null) return null;
                var rig = ClipRig.TryBuild(player, fallClip);
                if (rig == null) return null;

                var pose = player.gameObject.AddComponent<DownedPose>();
                float entry = opening == Opening.Settle ? SettleEntrySeconds : LateJoinEntrySeconds;
                // A body lying rolled toward its side has further to turn onto its back.
                if (opening != Opening.Fall && DragGrip.TryBackTilt(player, out float tilt)) entry += TiltEntrySeconds * Mathf.Clamp01(tilt / 90f);
                pose.Setup(player, rig, fall, intro, loopClip, pool, entry, recorded);
                pose._elapsed = 0f;
                string what = opening == Opening.Settle ? "Settling" : opening == Opening.Loop ? "Resumed" : fall == BakedClip.CrouchFall ? "Falling from a crouch" : "Falling";
                RevivalDebugLog.LogDebug($"[DownedPose] {what} for {player.ProfileId} ({rig.Count} bones, {pose._ground.LimbCount} limbs)");
                return pose;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedPose] Could not start for {player.ProfileId}: {ex.Message}");
                return null;
            }
        }

        // fall: the fall to play, or null when lying already. intro: the clip played before the loop on a body that lies
        // (the settle), or null to start on the loop. entrySeconds: the blend in from the pose on screen when lying.
        private void Setup(Player player, ClipRig rig, BakedClip fall, BakedClip intro, BakedClip loop, PlayerPoolObject pool, float entrySeconds, DownedRagdollController.LocalFall recorded)
        {
            _player = player;
            _fall = fall ?? intro;
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
                // A recorded fall (the local body) starts from where the pelvis was when it went down.
                _facing = recorded != null ? recorded.Facing : Facing(player);
                // The reference pelvis: where the pelvis is now, less where the fall's first frame has it from the
                // reference (nothing for the standing fall, the drop to the crouch for the crouched one).
                _pelvisStart = (recorded != null ? recorded.Pelvis : _pelvis.position) - _facing * (fall.Pelvis[0] * rig.Scale);
                _ground.BeginAtRoot(_pelvisStart, _facing * Vector3.forward);
            }
            else
            {
                _entrySeconds = entrySeconds;
                // Lying where the ragdoll left it: turned so the first clip's spine points along the body's, and the
                // reference pelvis put where it would have been for that clip's first frame to land here (the settle and
                // the loop barely travel, so this holds for whatever frame it carries on from).
                var first = intro ?? loop;
                _rig.Evaluate(first, 0f, _pose);
                _facing = FacingOf(player, rig, _pose);
                _ground.BeginAtBody(_pelvis.position, _facing * Vector3.forward);
                Vector3 offset = _facing * (first.Pelvis[0] * rig.Scale);
                _pelvisStart = new Vector3(_pelvis.position.x - offset.x, _ground.StartGroundY + first.FloorHeight * rig.Scale, _pelvis.position.z - offset.z);
            }

            AddLimb(BoneNames.LeftUpperArm, BoneNames.LeftForearm, BoneNames.LeftPalm);
            AddLimb(BoneNames.RightUpperArm, BoneNames.RightForearm, BoneNames.RightPalm);
            AddLimb(BoneNames.LeftThigh, BoneNames.LeftCalf, BoneNames.LeftFoot);
            AddLimb(BoneNames.RightThigh, BoneNames.RightCalf, BoneNames.RightFoot);
            AddLimb(BoneNames.Neck, null, BoneNames.Head);

            var tracked = new List<Transform>();
            foreach (var spawner in pool.RigidbodySpawners)
                if (spawner != null) tracked.Add(spawner.transform);
            _motion = new MotionRecorder(tracked.ToArray());
        }

        private void AddLimb(string root, string mid, string end)
        {
            Transform r = Find(root), m = mid != null ? Find(mid) : null, e = Find(end);
            if (r == null || e == null || (mid != null && m == null)) return;
            _ground.AddLimb(r, m, e);
        }

        private Transform Find(string name) => BoneNames.Find(_rig.Bones, name);

        //====================[ Getting Up ]====================
        // The roll onto the front and where it turns and slides the body to.
        private sealed class GetUpState
        {
            public readonly BakedClip Clip;
            // The way the roll's spine points at its end, and where its pelvis is then (skeleton metres, character
            // frame).
            private readonly float _endSpineYaw;
            private readonly Vector3 _endTravel;
            // The placement the roll turns and slides from (where the body lay).
            private readonly Quaternion _lieFacing;
            private readonly Vector3 _lieStart;
            // The player's root and body yaw the roll ends on, as last given and as followed so far.
            private Vector3 _targetRoot;
            private float _targetYaw;
            // The whole turn from where the body lay to the aim (degrees, signed), kept continuous: taken the short
            // way each frame, a turn near half round would flip direction mid-roll as the aim wobbles across it.
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

            // Where the player is now (their root, and the yaw of their body); the roll ends on it.
            public void SetTarget(Vector3 root, float yaw)
            {
                _targetRoot = root;
                _targetYaw = yaw;
            }

            // This frame of the roll's turn and slide: the body's facing and where its pelvis goes on the flat. It
            // starts out where the body lay and ends with the roll's last spine along the player's yaw and its last
            // pelvis on the player's root. local: the clip's pelvis at this frame (skeleton metres, character frame);
            // at: seconds into the roll.
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

                // Turned about the pelvis.
                facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(lieYaw + _turn * w);
                return Vector3.Lerp(lie, aim, w);
            }
        }

        // Plays the roll onto the front from the pose on screen, turning and sliding the body from where it lies (or
        // is falling to) onto the player's root and body yaw. False when there is no roll to play; the pose then
        // carries on as it was.
        public bool BeginGetUp(Vector3 root, float yaw) => BeginGetUp(root, yaw, GetUpEntrySeconds);

        private bool BeginGetUp(Vector3 root, float yaw, float entrySeconds)
        {
            var clip = BakedClip.GetUp;
            if (clip == null || !_rig.Plays(clip)) return false;
            float endSpineYaw = clip.SpineYaw(clip.Frames - 1);
            if (float.IsNaN(endSpineYaw)) return false;

            // A sequence still playing is over: the player has the body back.
            EndSelfStim();

            // The fall, the loop and the roll share the reference frame, so the placement the body lies at now is
            // the roll's as well.
            _getUp = new GetUpState(clip, endSpineYaw, clip.Pelvis[clip.Frames - 1] * _rig.Scale, _facing, _pelvisStart, root, yaw);
            _fall = null;

            for (int k = 0; k < _rig.Count; k++) _captured[k] = _rig.Bones[k].localRotation;
            _pelvisFrom = _pelvis.position;
            _entrySeconds = entrySeconds;
            _elapsed = 0f;
            _sinceStart = 0f;
            RevivalDebugLog.LogDebug($"[DownedPose] Getting up ({clip.Duration:F2}s) onto yaw {yaw:F0}, turning {_getUp.Turn:F0}");
            return true;
        }

        // Where the player is now (their root, and the yaw of their body); the roll ends on it.
        public void SetGetUpTarget(Vector3 root, float yaw) => _getUp?.SetTarget(root, yaw);

        // This frame of the roll's turn and slide, on the flat (see GetUpState.Aim); turns _facing, and the ground
        // follow's smoothed slope direction with it, or it would read the turn as a slope and lag it.
        private Vector3 AimGetUp(Vector3 local, float at)
        {
            Vector3 pelvis = _getUp.Aim(local, at, out Quaternion facing);
            // The ground is looked for from the player's height, which may change as they crawl.
            _ground.RootY = _getUp.AimRoot.y;
            _ground.SurfaceDirection = facing * Quaternion.Inverse(_facing) * _ground.SurfaceDirection;
            _facing = facing;
            return pelvis;
        }

        //====================[ Self Stim ]====================
        // Blend into the sequence, and out of it when a cancel cuts it short (s).
        private const float SelfStimEntrySeconds = 0.25f;
        private const float SelfStimCutSeconds = 0.3f;

        // Takes a body lying prone (EFT's own animation, which the caller has already switched off) and plays a sequence on it that
        // begins with the roll onto its back, placed so the roll ends the way the roll onto the front does: on the player's root, with
        // its spine along their body yaw. Null when there is nothing to play.
        public static DownedPose TryStartSelfStimFromProne(Player player, PlayerPoolObject pool, SelfStimSequence sequence, Vector3 root)
        {
            if (sequence == null || !sequence.FromProne) return null;
            var pose = TryStart(player, pool, Opening.Loop, null);
            if (pose == null) return null;
            if (pose.PlaceForProneRoll(root, sequence.Yaw) && pose.BeginSelfStim(sequence)) return pose;
            pose.Stop();
            return null;
        }

        // Puts the body down for a roll onto the back that ends where the roll onto the front begins: the roll's last frame, played
        // as the first, lies on `root` along `yaw`. The same placement GetUpState.Aim reaches at the end of the roll, held for all of it.
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

        // Starts the sequence on this pose, blended in from the pose on screen. False when it cannot be played (a roll or a fall is
        // playing, the clips do not share this rig).
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

        // The key was let go (SelfStimSequence.Cancel).
        public void CancelSelfStim(float now) => _stim?.Cancel(now);

        // The sequence is over, or cut short by a cancel or a get-up: its hand and pen go, the loop carries on.
        public void EndSelfStim()
        {
            _stim = null;
            _stimNeedle = Needle.Out;
            _stimPen = false;
            _stimHandFrame = -1f;
            _stimHands?.Dispose();
            _stimHands = null;
        }

        // Blends from the pose on screen into whatever plays next, over `seconds`.
        private void Recapture(float seconds)
        {
            for (int k = 0; k < _rig.Count; k++) _captured[k] = _rig.Bones[k].localRotation;
            _pelvisFrom = _pelvis.position;
            _entrySeconds = seconds;
            _sinceStart = 0f;
        }

        // The sequence's clip and time in it this frame. False once it is over (then the loop plays).
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

            // A hold too short for a hold segment goes straight from the takeout to the put away: that is one knock, the
            // withdrawal, since the needle was in and out within a frame.
            if (segment == SelfStimSequence.Segment.Hold && _stimNeedle == Needle.Out)
            {
                _stimNeedle = Needle.In;
                DownedVoiceController.OnStimNeedle(_player);
            }
            else if (segment == SelfStimSequence.Segment.PutAway && _stimNeedle != Needle.Withdrawn)
            {
                _stimNeedle = Needle.Withdrawn;
                DownedVoiceController.OnStimNeedle(_player);
            }
            return true;
        }

        //====================[ Root Anchor ]====================
        // The body lies where the player's root is, on every peer. The pose is placed in the world once (from the
        // body it started on) and Fika moves the player's root on its own afterwards, by the owner's knockback or
        // whatever this peer saw of the player, each later than the owner by that peer's own delay: left alone, the
        // body ends up off the real root (the hit colliders, the revive prompt) and off every other peer's copy.
        // So once the fall is over the pelvis is eased onto the root plus where it lies from the root (the owner
        // recorded it going down), level only: the ground follow keeps the height.
        private const float AnchorFollowRate = 6f;
        // The anchor holds while the root is still moving faster than this (m/s): the owner's knockback is still arriving
        // (each peer sees it late), and easing onto where the root is now would slide the body back first.
        private const float AnchorRootRestSpeed = 0.1f;
        // A yaw closer than this to the declared one (deg) is the declared one.
        private const float AnchorYawTolerance = 0.05f;

        // The root as last seen, for its speed.
        private Vector3 _rootSeen;
        private bool _hasRootSeen;

        // The lying reference frame's pelvis (skeleton metres, character frame): where _pelvisStart + _facing * this is
        // the pelvis of the body at rest, before the loop's own small sway.
        private Vector3 AnchorReferenceLocal => _loop.Pelvis[0] * _rig.Scale;

        // The pose's facing (yaw) for a body whose spine (pelvis to shoulders) points at spineYaw: the loop's own spine,
        // in its character frame, turned onto it. False when there is no loop or its spine cannot be read.
        public static bool TryFacingFromSpine(float spineYaw, out float facing)
        {
            facing = 0f;
            var loop = BakedClip.Loop;
            if (loop == null) return false;
            float clipSpine = loop.SpineYaw(0);
            if (float.IsNaN(clipSpine)) return false;
            facing = spineYaw - clipSpine;
            return true;
        }

        // Turns the body about its resting pelvis onto a yaw, the ground follow's slope direction with it (as the roll does).
        private void TurnAboutPelvis(float yaw)
        {
            Vector3 local = AnchorReferenceLocal;
            Vector3 pelvis = _pelvisStart + _facing * local;
            Quaternion facing = global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(yaw);
            _ground.SurfaceDirection = facing * Quaternion.Inverse(_facing) * _ground.SurfaceDirection;
            _facing = facing;
            _pelvisStart = pelvis - _facing * local;
        }

        private void AnchorToRoot()
        {
            // Not while the fall (or the settle) plays, nor over the roll: it aims at the root itself.
            if (_getUp != null || (_fall != null && _elapsed < _fall.Duration)) return;
            // Nor over the self-stim sequence, nor after its roll put the body down from the root.
            if (_stim != null || _stimPlaced) return;
            if (_player == null || !RMSession.TryGetPlayerState(_player.ProfileId, out var st)) return;

            Vector3 want;
            if (st.HasDropRest)
            {
                // Put down after a drag: the declared rest itself, on every peer the same, whenever this peer sees the
                // owner's real body arrive under it (DownedRagdollController moves it there). Every peer's ragdoll settled
                // turned its own way; all ease onto the declared yaw.
                if (Mathf.Abs(Mathf.DeltaAngle(_facing.eulerAngles.y, st.DropFacingYaw)) > AnchorYawTolerance)
                    TurnAboutPelvis(Mathf.LerpAngle(_facing.eulerAngles.y, st.DropFacingYaw, global::KeepMeAlive.Animation.Shared.MathUtil.Follow(AnchorFollowRate)));
                want = st.DropRestPelvis;
            }
            else if (!string.IsNullOrEmpty(st.DropDeclarerId)
                     && Time.time - st.DropReleaseTime < DownedDragController.DropRestWaitSeconds)
            {
                // Let go of, and whoever let go has not said where it came to rest yet: it lies where it settled here.
                return;
            }
            else
            {
                if (!st.HasLyingOffset) return;

                // Wait for the root to come to rest (it is the owner's knockback arriving, late by this peer's delay).
                Vector3 root = _player.Position;
                float dt = Time.deltaTime;
                bool moving = !_hasRootSeen || (dt > 0f && (root - _rootSeen).magnitude / dt > AnchorRootRestSpeed);
                _rootSeen = root;
                _hasRootSeen = true;
                if (moving) return;

                want = root + _facing * new Vector3(st.LyingOffset.x, 0f, st.LyingOffset.y);
            }
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
            CaptureAnimatedChest();
            CaptureGameView();
            try
            {
                AnchorToRoot();
                Pose(_elapsed);
                _stimHands?.Apply(_stim != null ? _stimHandFrame : -1f, _stim != null && _stimPen, Time.deltaTime);
                _motion.Record();
                FollowCamera(null);
            }
            catch (Exception ex)
            {
                // Hold whatever is on screen; BodyRagdoll's physics can still take over on a grab.
                Plugin.LogSource.LogWarning($"[DownedPose] Playback failed: {ex.Message}");
                enabled = false;
            }
        }

        //====================[ First Person Camera ]====================
        // The game copies its Cam (mouse look, the game's prone view) to the camera in its own late pass, before this writes the
        // bones: the view would face the ground while the body lies on its back. So the camera is put on the head after the
        // pose, and again just before it renders. EFT's own prone (a body settled on its front) never runs this: its view stays
        // the game's.
        private void OnEnable() => Camera.onPreCull += FollowCamera;
        private void OnDisable() => Camera.onPreCull -= FollowCamera;

        // How far ahead of the player's root the chest is on EFT's own prone body (the animation underneath, as it stands before
        // this writes the bones). DownedDragController pulls the root after the chest by this much: measured from the posed body
        // instead, which lies wherever the clip puts it, the root would be pulled onto the dragger.
        public float AnimatedChestAhead { get; private set; }
        public bool HasAnimatedChest { get; private set; }

        private void CaptureAnimatedChest()
        {
            if (_player == null || !_player.IsYourPlayer || _player.MovementContext == null) return;
            Transform chest = DragGrip.Chest(_player);
            if (chest == null) return;
            float yaw = _player.MovementContext.TransformRotation.eulerAngles.y;
            AnimatedChestAhead = Vector3.Dot(chest.position - _player.Position, global::KeepMeAlive.Animation.Shared.MathUtil.YawForward(yaw));
            HasAnimatedChest = true;
        }

        // While rolling, the game's view as the game has it this frame: the body animator runs under the roll (WarmAnimator)
        // and this reads before Pose writes the bones, so Spine3 and the CameraPosition under it are the game's.
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

        // How far the camera has slid from the head to the game's view (0 to 1, eased in and out): 0 until
        // RollCameraSlideStart into the roll, 1 at its end and while it holds its last frame.
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
                if (_cameraFailed || player == null || !player.IsYourPlayer || !player.FirstPersonPointOfView || DownedSpectate.IsActive) return;
                if (!CameraManager.Exist) return;
                var main = CameraManager.Instance.Camera;
                if (main == null || (camera != null && camera != main)) return;
                var head = player.PlayerBones != null && player.PlayerBones.Head != null ? player.PlayerBones.Head.Original : null;
                if (head == null) return;
                // The pose lies on its back, but the game still has the player prone: its look would face the ground. So the
                // view is the head's, as LocalBodyCamera has it for the ragdoll (and kept off walls the fall may carry it through).
                // Over the end of the roll it slides to the game's own view.
                LocalBodyCamera.Place(player, head, main, RollCameraSlide(), _gameViewPosition, _gameViewRotation);
            }
            catch (Exception ex)
            {
                _cameraFailed = true;
                Plugin.LogSource.LogWarning($"[DownedPose] Camera follow failed: {ex.Message}");
            }
        }

        // The clip at time t (the fall, then the loop over and over; or the roll, held on its last frame), on the
        // ground, cross-fading in from the pose on screen. Parents are posed first, so each bone's world rotation is
        // set against its parent's final pose.
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
            // Blending in from the pose on screen; the limbs only meet the ground once it is in.
            float fade = FadeIn;

            // Where the clip puts the pelvis, then the ground under it: the pose is turned onto the slope there.
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

            // The pelvis at the clip's height above the ground under it, measured along the surface the pose is
            // turned onto (on flat ground, the clip's own path).
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

        // How far into its blend from the pose on screen this is (0 to 1, eased in and out).
        private float FadeIn => Mathf.SmoothStep(0f, 1f, _sinceStart / _entrySeconds);

        //====================[ Ragdoll Handoff ]====================
        // How fast the bone was moving over the last frame, for the ragdoll to carry on with.
        public bool TryGetVelocity(Transform bone, out Vector3 velocity, out Vector3 angularVelocity) =>
            _motion.TryGetVelocity(bone, out velocity, out angularVelocity);

        private void OnDestroy() => _stimHands?.Dispose();

        // Stops playing and leaves the bones where they are.
        public void Stop()
        {
            enabled = false;
            Destroy(this);
        }

        //====================[ Body Checks ]====================
        // Whether the body is standing or crouched, so a fall would make sense from it.
        public static bool IsUpright(Player player)
        {
            var bones = player.PlayerBones;
            Transform pelvis = bones != null && bones.Pelvis != null ? bones.Pelvis.Original : null;
            Transform head = bones != null && bones.Head != null ? bones.Head.Original : null;
            return pelvis != null && head != null && head.position.y - pelvis.position.y >= MinUprightHeadHeight;
        }

        // The fall this body goes down with: from standing or from a crouch, by how high its pelvis is above its feet;
        // null when it is already down or there are no clips. The player and the observers both work it out from the
        // body as EFT has it on screen when it goes down, so they pick the same one.
        public static BakedClip FallFor(Player player)
        {
            var stand = BakedClip.Fall;
            if (stand == null || !IsUpright(player)) return null;

            var crouch = BakedClip.CrouchFall;
            if (crouch == null || !crouch.SharesRig(stand) || !TryGetScale(player, out float scale)) return stand;

            float height = player.PlayerBones.Pelvis.Original.position.y - player.Transform.position.y;
            return height < CrouchedHeightShare * stand.FloorHeight * scale ? crouch : stand;
        }

        // The yaw that turns the loop (as evaluated in pose) so its spine points the way the body's does now.
        private static Quaternion FacingOf(Player player, ClipRig rig, Quaternion[] pose)
        {
            var bones = player.PlayerBones;
            Transform pelvis = rig.Pelvis;
            Transform head = bones.Head.Original;
            int p = rig.IndexOf(pelvis);
            Vector3 spine = head.position - pelvis.position;
            // The same spine as the loop's pelvis carries it.
            Vector3 loopSpine = pose[p] * (Quaternion.Inverse(pelvis.rotation) * spine);
            Vector3 have = Vector3.ProjectOnPlane(spine, Vector3.up);
            Vector3 want = Vector3.ProjectOnPlane(loopSpine, Vector3.up);
            if (have.sqrMagnitude < 1e-4f || want.sqrMagnitude < 1e-4f) return Facing(player);
            return Quaternion.AngleAxis(Vector3.SignedAngle(want, have, Vector3.up), Vector3.up);
        }

        //====================[ Travel ]====================
        // The fall carries the body back about a metre and a half. The downed player is moved along the same path
        // on their own client (DownedRagdollController), so the replicated position ends up where the body lands
        // on every observer. Both sides work it out from the same things: the clip, the player's yaw, and the
        // skeleton's size.

        // The way the fall goes: the yaw of the player root, which observers get from the owner.
        public static Quaternion Facing(Player player) => global::KeepMeAlive.Animation.Shared.MathUtil.Yaw(player.Transform.rotation.eulerAngles.y);

        // Clip metres to this skeleton's metres, from the pelvis height.
        public static bool TryGetScale(Player player, out float scale) => ClipRig.TryGetScale(player, BakedClip.Fall, out scale);

        // Horizontal offset of the body from where it went down, t seconds into the fall (a clip FallFor gave). The
        // pelvis path is measured from the reference frame, so a fall from a crouch starts offset by the drop to it;
        // only the part from its own first frame is the body's travel.
        public static Vector3 Travel(BakedClip clip, float t, Quaternion facing, float scale)
        {
            clip.FrameAt(t, out int a, out int b, out float f);
            Vector3 offset = (Vector3.Lerp(clip.Pelvis[a], clip.Pelvis[b], f) - clip.Pelvis[0]) * scale;
            offset.y = 0f;
            return facing * offset;
        }
    }
}
