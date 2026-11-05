//====================[ Imports ]====================
using System;
using System.Collections;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivePresentation ]====================
    // Local-only "silent inventory" presentation: screen blur plus the inventory-open pose used
    // while unconscious and while reviving. Player.SetInventoryOpened only drives animation (the
    // inventory screen never opens).
    internal static class RevivePresentation
    {
        private const float EquipTimeoutSeconds = 2f;
        private const float EquipSettleSeconds = 0.3f;
        private const float UnconsciousHandsWaitSeconds = 3f;

        //====================[ Blur ]====================
        // CameraManager.Blur is a plain toggle (not refcounted), so every call goes through here
        // so I can keep IsSilentReviveBlurActive in step with the active blur.
        internal static void SetBlur(RMPlayer st, bool on)
        {
            if (st.IsSilentReviveBlurActive == on || CameraManager.Instance == null) return;
            CameraManager.Instance.Blur(on);
            st.IsSilentReviveBlurActive = on;
        }

        //====================[ Unconscious ]====================
        internal static void ApplyUnconscious(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            ReviveDebug.Log("Unconscious_Enter", player.ProfileId, true, null);

            try
            {
                var st = RMSession.GetPlayerState(player.ProfileId);
                SetBlur(st, true);

                // Raise the command-block flag now so the TranslateCommand patch blocks input this
                // frame, then delay the pose to let the empty-hands transition settle.
                if (!st.IsSilentInventoryAnimActive)
                {
                    st.IsSilentInventoryAnimActive = true;
                    st.UnconsciousOpenCoroutine = Plugin.StaticCoroutineRunner.StartCoroutine(DelayedInventoryOpen(player, st));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveAnim] ApplyUnconscious error: {ex.Message}");
            }
        }

        private static IEnumerator DelayedInventoryOpen(Player player, RMPlayer st)
        {
            yield return new WaitForSeconds(1.0f);

            // Onto empty hands only; a pose on a firearm still dropping lands mid-hide.
            float waited = 0f;
            while (player != null && st.State == RMState.BleedingOut && st.IsSilentInventoryAnimActive
                   && !(PlayerUtils.IsEmptyHanded(player) && PlayerUtils.HasUsableHands(player)) && waited < UnconsciousHandsWaitSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            st.UnconsciousOpenCoroutine = null;
            if (player == null || st.State != RMState.BleedingOut || !st.IsSilentInventoryAnimActive) yield break;
            if (!PlayerUtils.HasUsableHands(player))
            {
                ReviveDebug.Log("Unconscious_InvOpenSkipped", player.ProfileId, true, PlayerUtils.DescribeHands(player));
                yield break;
            }

            player.SetInventoryOpened(true);
            ReviveDebug.Log("Unconscious_InvOpen", player.ProfileId, true, null);
        }

        //====================[ Revive Animation ]====================
        internal static void PlayReviveAnimation(Player player, RMPlayer st)
        {
            if (player == null || st == null || !player.IsYourPlayer || st.State != RMState.Reviving) return;

            if (st.ReviveEffectsCoroutine != null)
            {
                Plugin.StaticCoroutineRunner.StopCoroutine(st.ReviveEffectsCoroutine);
            }
            st.ReviveEffectsCoroutine = Plugin.StaticCoroutineRunner.StartCoroutine(ReviveAnimationCoroutine(player, st));
        }

        private static bool IsStillReviving(Player player, RMPlayer st) => player != null && st.State == RMState.Reviving;

        private static IEnumerator ReviveAnimationCoroutine(Player player, RMPlayer st)
        {
            ReviveDebug.Log("ReviveEffects_Start", player.ProfileId, true, "source=Self");
            if (SyncedServerConfigStore.Config.Gameplay.Downed.BlockUiWhenDowned) Hud.SetInputBlocked(true);
            SetBlur(st, true);
            // Raise the command block before the weapon is drawn, not after the pose: otherwise the
            // player can fire, aim, reload and move for the whole draw.
            st.IsSilentInventoryAnimActive = true;

            //====================[ 1. Gun In Hand ]====================
            // The hands settle first; an equip queued on a change in flight is skipped or stalls.
            bool idle = false;
            yield return InventoryUtils.WaitForHandsIdle(player, EquipTimeoutSeconds, "ReviveEffects_GunEquip", ok => idle = ok);
            if (!IsStillReviving(player, st)) yield break;
            if (!idle || !PlayerUtils.HasUsableHands(player))
            {
                yield return PlayerRestorations.RecoverHandsCoroutine(player, "revive draw", equipWeapon: false);
                if (!IsStillReviving(player, st)) yield break;
            }

            // The equip block is open only for this request.
            bool equipped = false;
            string equipError = null;
            st.AllowWeaponEquipForReviveAnim = true;
            try
            {
                player.SetFirstAvailableItem((Result<IHandsController> result) =>
                {
                    equipped = true;
                    equipError = result.Error;
                });
            }
            finally
            {
                st.AllowWeaponEquipForReviveAnim = false;
            }

            float elapsed = 0f;
            while (!equipped && elapsed < EquipTimeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            if (!IsStillReviving(player, st)) yield break;

            // Wait for the hands change to settle.
            if (equipped && string.IsNullOrEmpty(equipError))
            {
                yield return InventoryUtils.WaitForHandsIdle(player, EquipTimeoutSeconds, "ReviveEffects_GunEquipSettle");
                if (!IsStillReviving(player, st)) yield break;
            }

            // The pose goes on usable hands only.
            if (!equipped || !string.IsNullOrEmpty(equipError) || !PlayerUtils.HasUsableHands(player))
            {
                Plugin.LogSource.LogWarning($"[ReviveAnim] Revive draw did not settle (callback={equipped} error='{equipError}'; {PlayerUtils.DescribeHands(player)})");
                yield return PlayerRestorations.RecoverHandsCoroutine(player, "revive draw result", equipWeapon: false);
                if (!IsStillReviving(player, st)) yield break;
            }

            //====================[ 2. Inventory Pose ]====================
            // Apply the pose after GamePlayerOwner.SetHandsController finishes the hands change.
            yield return new WaitForSeconds(EquipSettleSeconds);
            if (!IsStillReviving(player, st)) yield break;
            if (!PlayerUtils.HasUsableHands(player))
            {
                ReviveDebug.Log("ReviveEffects_PoseSkipped", player.ProfileId, true, PlayerUtils.DescribeHands(player));
                st.ReviveEffectsCoroutine = null;
                yield break;
            }

            player.SetInventoryOpened(true);
            st.ReviveEffectsCoroutine = null;
            ReviveDebug.Log("ReviveEffects_Done", player.ProfileId, true, null);
        }

        //====================[ Stop ]====================
        internal static void Stop(Player player, RMPlayer st, string reason)
        {
            if (player == null || st == null || !player.IsYourPlayer) return;
            ReviveDebug.Log("StopSilentInvAnim_Enter", player.ProfileId, true, $"reason={reason} invActive={st.IsSilentInventoryAnimActive} blurActive={st.IsSilentReviveBlurActive}");

            StopCoroutine(st.ReviveEffectsCoroutine);
            st.ReviveEffectsCoroutine = null;
            StopCoroutine(st.UnconsciousOpenCoroutine);
            st.UnconsciousOpenCoroutine = null;

            bool hadSilentInventory = st.IsSilentInventoryAnimActive;
            try
            {
                // The game's flag counts too. Not under the real inventory screen.
                if ((hadSilentInventory || player.IsInventoryOpened) && !InventoryScreenShowing())
                {
                    player.SetInventoryOpened(false);
                    // A weapon still drawing ignores the close; send it again once the hands settle.
                    if (!PlayerUtils.HasUsableHands(player))
                        Plugin.StaticCoroutineRunner.StartCoroutine(CloseAgainWhenHandsSettle(player, st));
                }
                SetBlur(st, false);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveAnim] Stop error: {ex.Message}");
            }
            finally
            {
                // Never leave command-block latches active past revive-stop intent.
                st.AllowWeaponEquipForReviveAnim = false;
                st.IsSilentInventoryAnimActive = false;
            }
        }

        private const float CloseAgainWaitSeconds = 3f;

        private static IEnumerator CloseAgainWhenHandsSettle(Player player, RMPlayer st)
        {
            float waited = 0f;
            while (player != null && !PlayerUtils.HasUsableHands(player) && waited < CloseAgainWaitSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            // Not if a new presentation opened it since, or the inventory screen is up.
            if (player == null || !PlayerUtils.IsAlive(player) || st.IsSilentInventoryAnimActive || InventoryScreenShowing()) yield break;
            try { player.SetInventoryOpened(false); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[ReviveAnim] Re-close of the inventory pose failed: {ex.Message}"); }
        }

        private static bool InventoryScreenShowing()
        {
            var mgr = EFT.UI.Screens.EftScreenManager.Instance;
            return mgr != null && mgr.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Inventory);
        }

        private static void StopCoroutine(Coroutine routine)
        {
            if (routine != null) Plugin.StaticCoroutineRunner.StopCoroutine(routine);
        }
    }
}
