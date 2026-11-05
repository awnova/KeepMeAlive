//====================[ Imports ]====================
using System;
using System.Collections;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ PlayerRestorations ]====================
    internal static class PlayerRestorations
    {
        //====================[ Movement Restoration ]====================
        // Speed is not restored here: downed/invulnerability speed is a state speed limit owned by
        // DownedMovementController.TickSpeedLimit. Sprint isn't touched: EnableSprint(true) means sprint pressed, not allowed.
        public static void RestorePlayerMovement(Player player)
        {
            if (player is null || !player.IsYourPlayer)
            {
                return;
            }

            try
            {
                var mc = player.MovementContext;
                // Out of prone in the game's order: the flag, then the pose.
                if (mc.IsInPronePose) mc.IsInPronePose = false;
                mc.SetPoseLevel(1f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[PlayerRestorations] RestorePlayerMovement: {ex.Message}");
            }
        }

        //====================[ Weapon Restoration ]====================
        // A drop in progress still counts as holding a firearm, and the game skips a re-equip of it, so the hands must
        // also be usable.
        public static void RestorePlayerWeapon(Player player)
        {
            if (!PlayerUtils.IsLocalPlayer(player)) return;
            if (PlayerUtils.HoldsFirearm(player) && PlayerUtils.HasUsableHands(player)) return;
            Plugin.StaticCoroutineRunner.StartCoroutine(RecoverHandsCoroutine(player, "post-revive weapon restore", equipWeapon: true));
        }

        // Last check once the revive is over; recovers the hands only if stuck.
        public static void VerifyHands(Player player, string reason)
        {
            if (!PlayerUtils.IsLocalPlayer(player) || Plugin.StaticCoroutineRunner == null) return;
            Plugin.StaticCoroutineRunner.StartCoroutine(VerifyHandsNextFrame(player, reason));
        }

        private static IEnumerator VerifyHandsNextFrame(Player player, string reason)
        {
            yield return null;
            if (!PlayerUtils.IsAlive(player) || RMSession.IsPlayerCritical(player.ProfileId)) yield break;
            if (PlayerUtils.HasUsableHands(player)) yield break;
            yield return RecoverHandsCoroutine(player, reason, equipWeapon: !PlayerUtils.HoldsFirearm(player));
        }

        //====================[ Hands Recovery ]====================
        private const float RecoverIdleWaitSeconds = 2f;

        // FastForwardCurrentOperations finishes whatever the hands are in, as the game does in OnDead and DestroyController.
        // Then the weapon goes back if wanted.
        public static IEnumerator RecoverHandsCoroutine(Player player, string reason, bool equipWeapon)
        {
            if (!PlayerUtils.IsLocalPlayer(player) || !PlayerUtils.IsAlive(player)) yield break;

            bool idle = false;
            yield return InventoryUtils.WaitForHandsIdle(player, RecoverIdleWaitSeconds, $"RecoverHands:{reason}", ok => idle = ok);
            if (!PlayerUtils.IsAlive(player)) yield break;

            if (!idle || !PlayerUtils.HasUsableHands(player))
            {
                Plugin.LogSource.LogWarning($"[RecoverHands] {reason}: hands stuck ({PlayerUtils.DescribeHands(player)}); fast-forwarding the current hands operations");
                FastForward(player);
                yield return InventoryUtils.WaitForHandsIdle(player, RecoverIdleWaitSeconds, $"RecoverHands:{reason}:after", ok => idle = ok);
                if (!PlayerUtils.IsAlive(player)) yield break;
                Plugin.LogSource.LogInfo($"[RecoverHands] {reason}: after fast-forward ({PlayerUtils.DescribeHands(player)})");
            }

            if (!equipWeapon || RMSession.IsPlayerCritical(player.ProfileId)) yield break;
            if (PlayerUtils.HoldsFirearm(player) && PlayerUtils.HasUsableHands(player)) yield break;

            try
            {
                // Last weapon in hands, else the first available (as RevealWeapon).
                player.TrySetLastEquippedWeapon();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[RecoverHands] {reason}: equip error: {ex}");
            }
        }

        public static void FastForward(Player player)
        {
            try
            {
                if (player?.HandsController != null) player.FastForwardCurrentOperations();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[RecoverHands] FastForwardCurrentOperations error: {ex}");
            }
        }
    }
}
