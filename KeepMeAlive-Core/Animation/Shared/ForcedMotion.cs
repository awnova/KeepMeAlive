//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Shared
{
    //====================[ ForcedMotion ]====================
    // Moves a player's character controller by a given step regardless of the game's speed limit (a downed player is
    // limited to a crawl), for the knockback of the fall and for dragging the body along.
    internal static class ForcedMotion
    {
        // Downward speed that keeps the moved player on the ground (m/s).
        private const float GroundStickSpeed = 2f;

        // Moves the player by `horizontal` (its y is replaced by the ground-stick step) over dt seconds.
        public static void Apply(MovementContext mc, Vector3 horizontal, float dt)
        {
            Vector3 motion = horizontal;
            motion.y = -GroundStickSpeed * dt;

            var cc = mc.CharacterController;
            float savedLimit = cc.SpeedLimit;
            try
            {
                cc.SpeedLimit = -1f;
                mc.DirectApplyMotion(motion, dt);
            }
            finally
            {
                cc.SpeedLimit = savedLimit;
            }
        }
    }
}
