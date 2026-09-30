//====================[ Imports ]====================
using System;
using System.Collections;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ RevivePresentation ]====================
    // Local-only "silent inventory" presentation: screen blur plus the inventory-open pose used
    // while unconscious and while reviving. Player.SetInventoryOpened only drives animation (the
    // inventory screen never opens) and Fika's FikaPlayer override replicates it to observers.
    internal static class RevivePresentation
    {
        private const float EquipTimeoutSeconds = 2f;
        private const float EquipSettleSeconds = 0.3f;

        //====================[ Blur ]====================
        // CameraManager.Blur is a plain toggle (not refcounted), so every call goes through here
        // to keep IsSilentReviveBlurActive in step with what we actually switched on.
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
            st.UnconsciousOpenCoroutine = null;
            if (player == null || st.State != RMState.BleedingOut || !st.IsSilentInventoryAnimActive) yield break;

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
            ReviveDebug.Log("ReviveEffects_Start", player.ProfileId, true, $"source={(ReviveSource)st.ReviveRequestedSource}");
            if (SyncedServerConfigStore.Config.Gameplay.Revival.BlockUiWhenDowned) DownedUiBlocker.SetBlocked(true);
            SetBlur(st, true);

            //====================[ 1. Gun In Hand ]====================
            st.AllowWeaponEquipForReviveAnim = true;
            yield return ModUtils.WaitForInventoryFree(player, 2f, "ReviveEffects_GunEquip");
            if (!IsStillReviving(player, st)) yield break;

            // Wait for the hands transition callback before continuing the revive animation.
            bool equipped = false;
            player.SetFirstAvailableItem((Result<IHandsController> _) => equipped = true);
            float elapsed = 0f;
            while (!equipped && elapsed < EquipTimeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            //====================[ 2. Inventory Pose ]====================
            // Apply the pose after GamePlayerOwner.SetHandsController finishes the hands change.
            yield return new WaitForSeconds(EquipSettleSeconds);
            if (!IsStillReviving(player, st)) yield break;

            player.SetInventoryOpened(true);
            st.IsSilentInventoryAnimActive = true;
            st.ReviveEffectsCoroutine = null;
            ReviveDebug.Log("ReviveEffects_Done", player.ProfileId, true, $"equipped={equipped}");
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
                if (hadSilentInventory)
                {
                    player.SetInventoryOpened(false);
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

        private static void StopCoroutine(Coroutine routine)
        {
            if (routine != null) Plugin.StaticCoroutineRunner.StopCoroutine(routine);
        }
    }
}
