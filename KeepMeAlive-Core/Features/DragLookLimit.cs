//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DragLookLimit ]====================
    // The local dragger's look is limited up and down for as long as they pick up and hold a body (like the defibrillator hold's
    // look clamp, CrouchHoldState), by the limits here and in KeepMeAliveSettings. Movement stays free: they walk with the body. Yaw is
    // left as it is. The game's own states put the limits back when they exit, so the limit is held every tick; letting go
    // restores what the player had.
    internal static class DragLookLimit
    {
        // How far below the horizon the look may go (degrees; baked from the slider tuned in game, 27 on 2026-10-15, 25 on 2026-10-16).
        // The look up limit is still KeepMeAliveSettings.DRAG_LOOK_UP_LIMIT.
        private const float LookDownLimit = 25f;

        private static MovementContext _mc;
        private static Vector2 _savedYawLimit;
        private static Vector2 _savedPitchLimit;

        //====================[ Control ]====================
        public static void Begin(Player player)
        {
            if (_mc != null) Restore();
            var mc = player?.MovementContext;
            if (mc == null) return;

            _mc = mc;
            _savedYawLimit = mc._yawLimit;
            _savedPitchLimit = mc._pitchTargetLimit;
            Hold();
        }

        // Every tick while dragging: the limit stays whatever the game does.
        public static void Hold()
        {
            if (_mc == null) return;
            Vector2 wanted = Wanted();
            if (_mc._pitchTargetLimit != wanted) _mc.SetPitchSmoothly(wanted);
        }

        public static void Restore()
        {
            var mc = _mc;
            _mc = null;
            if (mc == null) return;
            mc.SetRotationLimit(_savedYawLimit, _savedPitchLimit);
        }

        // The game's pitch is negative looking up and positive looking down (standing: -90..90).
        private static Vector2 Wanted() => new Vector2(-KeepMeAliveSettings.DRAG_LOOK_UP_LIMIT.Value, LookDownLimit);
    }
}
