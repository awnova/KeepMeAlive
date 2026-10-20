//====================[ Imports ]====================
using System;
using EFT;
using KeepMeAlive.Animation.Hands;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ CrouchHoldState ]====================
    // Timed hold for team revive / team heal: movement frozen, crouched (prone stays prone), look clamped to a small
    // cone (a revive first turns the player to face the body's chest, where it lay when the hold began).
    // Runs as an overriding movement state; the crouch reaches observers through Fika's synced pose level.
    // Ends on its own after the hold time, or on Cancel (interact key released, inventory, menus, being downed).
    internal sealed class CrouchHoldState : MovementState
    {
        // Look clamp around the direction faced when the hold starts (degrees).
        private const float YawHalfRange = 7.5f;
        private const float PitchDown = 10f;
        private const float PitchUp = 3f;
        // Turning to face the body: the turn eases out (1/s, with a floor so it finishes) and stops this close to it (degrees).
        private const float TurnGain = 8f;
        private const float MinTurnSpeed = 60f;
        private const float TurnDone = 0.3f;
        // A turn still going after this long (a prone body that cannot swing) is given up on (s).
        private const float MaxTurnSeconds = 1.5f;

        private readonly float _duration;
        // The body the player turns to face (null: they keep the way they were looking), and where that is: worked out once,
        // at the first tick, so a body that settles afterwards does not move the player.
        private readonly Player _self, _facing;
        private Vector2? _faceTarget;
        private bool _faced;
        private Action<bool> _onDone;
        private float _startTime;
        private float _savedPose;
        private Vector2 _savedYawLimit;
        private Vector2 _savedPitchLimit;

        private CrouchHoldState(MovementContext mc, Player self, float duration, Action<bool> onDone, Player facing) : base(mc)
        {
            _self = self;
            _facing = facing;
            Name = EPlayerState.Idle;
            AnimatorStateHash = -1; // keep the current body animation
            _duration = duration;
            _onDone = onDone;
        }

        //====================[ Entry Points ]====================
        // Starts a hold from standing/crouched or prone idle. False if the player can't start one right now.
        public static bool Begin(Player player, float seconds, Action<bool> onDone, Player facing = null)
        {
            var mc = player?.MovementContext;
            if (mc == null || mc.OverridenState != null || player.CurrentState is not IdlePlayerState) return false;

            mc.OverrideState(new CrouchHoldState(mc, player, seconds, onDone, facing));
            return true;
        }

        public static bool IsActive(Player player) => player?.CurrentState is CrouchHoldState;

        //====================[ State ]====================
        public override void Enter(bool isFromSameState)
        {
            base.Enter(isFromSameState);
            _savedPose = MovementContext.PoseLevel;
            _savedYawLimit = MovementContext._yawLimit;
            _savedPitchLimit = MovementContext._pitchTargetLimit;

            Vector2 rot = MovementContext.Rotation;
            MovementContext.SetTilt(0f);
            MovementContext.SetRotationLimit(
                new Vector2(rot.x - YawHalfRange, rot.x + YawHalfRange),
                new Vector2(rot.y - PitchDown, rot.y + PitchUp));
            if (!MovementContext.IsInPronePose) MovementContext.SetPoseLevel(0f);
            _startTime = Time.time;
        }

        public override void Exit(bool toSameState)
        {
            base.Exit(toSameState);
            MovementContext.SetRotationLimit(_savedYawLimit, _savedPitchLimit);
            if (!MovementContext.IsInPronePose) MovementContext.SetPoseLevel(_savedPose);

            // Something other than Finish ended the hold; still report it so the caller can clean up.
            var pending = _onDone;
            _onDone = null;
            pending?.Invoke(false);
        }

        public override void ManualAnimatorMoveUpdate(float deltaTime)
        {
            base.ManualAnimatorMoveUpdate(deltaTime);
            FaceBody(deltaTime);
            if (Time.time - _startTime >= _duration) Finish(true);
        }

        public override void Cancel() => Finish(false);

        //====================[ Facing ]====================
        // Turns the player (look and body) toward the body's chest once, easing in; the look clamp moves with them, leaving the
        // same small cone around where they end up. A prone player turns only where the game says the body can swing (the same
        // check as its own prone turn).
        private void FaceBody(float deltaTime)
        {
            if (_faced || _facing == null) return;
            if (Time.time - _startTime > MaxTurnSeconds)
            {
                _faced = true;
                return;
            }
            var mc = MovementContext;
            if (_faceTarget == null)
            {
                var camera = _self?.CameraPosition;
                var chest = DragGrip.Chest(_facing);
                if (camera == null || chest == null || (chest.position - camera.position).sqrMagnitude < 0.01f)
                {
                    _faced = true;
                    return;
                }
                Vector3 toChest = (chest.position - camera.position).normalized;
                // The look stays inside what the pose allows (the limits the hold started with).
                float chestPitch = -Mathf.Asin(Mathf.Clamp(toChest.y, -1f, 1f)) * Mathf.Rad2Deg;
                _faceTarget = new Vector2(Mathf.Atan2(toChest.x, toChest.z) * Mathf.Rad2Deg,
                    Mathf.Clamp(chestPitch, _savedPitchLimit.x, _savedPitchLimit.y));
            }
            float targetYaw = _faceTarget.Value.x, targetPitch = _faceTarget.Value.y;

            Vector2 look = mc.Rotation;
            float yawGap = Mathf.DeltaAngle(look.x, targetYaw);
            float pitchGap = targetPitch - look.y;
            if (Mathf.Abs(yawGap) < TurnDone && Mathf.Abs(pitchGap) < TurnDone)
            {
                _faced = true;
                return;
            }

            float yawStep = Mathf.Min(Mathf.Abs(yawGap), Mathf.Max(MinTurnSpeed, TurnGain * Mathf.Abs(yawGap)) * deltaTime) * Mathf.Sign(yawGap);
            float pitchStep = Mathf.Min(Mathf.Abs(pitchGap), Mathf.Max(MinTurnSpeed, TurnGain * Mathf.Abs(pitchGap)) * deltaTime) * Mathf.Sign(pitchGap);
            if (mc.IsInPronePose && !mc.IsAbleToRotateProne(Vector3.zero, yawStep, Quaternion.Euler(0f, 2f * yawStep, 0f), _self.PlayerBones.Spine1, out _))
                yawStep = 0f;

            float yaw = look.x + yawStep;
            if (yaw > 360f) yaw -= 360f;
            else if (yaw < -360f) yaw += 360f;
            float pitch = look.y + pitchStep;
            // The clamp first (it would pull the look back to the old cone), then the look, then the body under it.
            mc.SetRotationLimit(new Vector2(yaw - YawHalfRange, yaw + YawHalfRange), new Vector2(pitch - PitchDown, pitch + PitchUp));
            mc.Rotation = new Vector2(yaw, pitch);
            if (!Mathf.Approximately(yawStep, 0f)) mc.ApplyRotation(Quaternion.AngleAxis(yawStep, Vector3.up) * mc.TransformRotation);
        }

        // Leaves the state first, so the callback runs with the player already out of the hold.
        private void Finish(bool ok)
        {
            if (!ReferenceEquals(MovementContext.OverridenState, this)) return;

            var done = _onDone;
            _onDone = null;
            MovementContext.ExitOverridenState();
            done?.Invoke(ok);
        }

        //====================[ Blocked Input ]====================
        public override void ChangePose(float poseDelta) { }
        public override void SetTilt(float tilt) { }
        public override void Prone() { }
        public override void Plant(bool enabled, bool multitool, float plantTime, Action<bool> action) { }
        public override void Pickup(bool enabled, Action action) { }
        public override void Examine(bool enabled, Action action) { }
    }
}
