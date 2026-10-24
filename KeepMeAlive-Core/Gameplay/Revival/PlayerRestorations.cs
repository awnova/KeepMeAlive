//====================[ Imports ]====================
using System;
using System.Collections;
using Comfort.Common;
using EFT;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ PlayerRestorations ]====================
    internal static class PlayerRestorations
    {
        //====================[ Movement Restoration ]====================
        // Speed is not restored here: downed/invulnerability speed is a state speed limit owned by
        // DownedMovementController.TickSpeedLimit.
        public static void RestorePlayerMovement(Player player, bool forceStandingPose = true)
        {
            if (player is null || !player.IsYourPlayer)
            {
                return;
            }

            try
            {
                if (forceStandingPose)
                {
                    player.MovementContext.SetPoseLevel(1f);
                    player.MovementContext.IsInPronePose = false;
                }
                player.MovementContext.EnableSprint(true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[PlayerRestorations] RestorePlayerMovement: {ex.Message}");
            }
        }

        public static void RestorePlayerWeapon(Player player)
        {
            if (player is null || !player.IsYourPlayer) return;

            // Force hands to equip something if they are empty or stuck in a non-weapon state.
            // I defer this to a coroutine to wait out any in-flight inventory operation.
            // (e.g. a self-defib still being consumed) before racing another equip
            // against it - see RestorePlayerWeaponCoroutine.
            if (!(player.HandsController is IFirearmHandsController))
            {
                Plugin.StaticCoroutineRunner.StartCoroutine(RestorePlayerWeaponCoroutine(player));
            }
        }

        private static IEnumerator RestorePlayerWeaponCoroutine(Player player)
        {
            yield return ModUtils.WaitForInventoryFree(player, 2f, "RestorePlayerWeapon");

            try
            {
                if (player != null && player.HealthController != null && player.HealthController.IsAlive
                    && !(player.HandsController is IFirearmHandsController))
                {
                    player.SetFirstAvailableItem((Result<IHandsController> _) => { });
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[PlayerRestorations] RestorePlayerWeapon error: {ex}");
            }
        }
    }
}
