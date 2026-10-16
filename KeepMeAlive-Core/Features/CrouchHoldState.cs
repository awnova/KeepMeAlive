//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ CrouchHoldState ]====================
    // Timed hold for team revive / team heal: movement frozen, crouched (prone stays prone), look clamped to a small
    // cone. Runs as an overriding movement state; the crouch reaches observers through Fika's synced pose level.
    // Ends on its own after the hold time, or on Cancel (interact key released, inventory, menus, being downed).
    internal sealed class CrouchHoldState : MovementState
    {
        // Look clamp around the direction faced when the hold starts (degrees).
        private const float YawHalfRange = 7.5f;
        private const float PitchDown = 10f;
        private const float PitchUp = 3f;

        private readonly float _duration;
        private Action<bool> _onDone;
        private float _startTime;
        private float _savedPose;
        private Vector2 _savedYawLimit;
        private Vector2 _savedPitchLimit;

        private CrouchHoldState(MovementContext mc, float duration, Action<bool> onDone) : base(mc)
        {
            Name = EPlayerState.Idle;
            AnimatorStateHash = -1; // keep the current body animation
            _duration = duration;
            _onDone = onDone;
        }

        //====================[ Entry Points ]====================
        // Starts a hold from standing/crouched or prone idle. False if the player can't start one right now.
        public static bool Begin(Player player, float seconds, Action<bool> onDone)
        {
            var mc = player?.MovementContext;
            if (mc == null || mc.OverridenState != null || player.CurrentState is not IdlePlayerState) return false;

            mc.OverrideState(new CrouchHoldState(mc, seconds, onDone));
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
            if (Time.time - _startTime >= _duration) Finish(true);
        }

        public override void Cancel() => Finish(false);

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
