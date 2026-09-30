//====================[ Imports ]====================
using System;
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ PostRevivalController ]====================
    // Remote players only get state/timer bookkeeping here. Anything touching input locks
    // (PlayerOwner.ignoredCommands is a single static set), pose, hands, speed or health must
    // only run for the local player, or a teammate's revive would unlock/alter *our* player.
    internal static class PostRevivalController
    {
        //====================[ Timers ]====================
        public static void TickInvulnerability(Player player, RMPlayer st)
        {
            if (st.State != RMState.Revived) return;

            if (st.InvulnerabilityTimer > 0f)
            {
                st.InvulnerabilityTimer -= Time.deltaTime;
                if (st.InvulnerabilityTimer > 0f) return;
            }

            EndInvulnerability(player, st);
        }

        public static void TickCooldown(Player player, RMPlayer st)
        {
            if (st.State != RMState.CoolDown) return;

            // A zero (or already elapsed) cooldown must still end, or the player stays in CoolDown
            // for the rest of the raid and every later lethal hit kills them outright.
            st.CooldownTimer -= Time.deltaTime;
            if (st.CooldownTimer > 0f) return;

            RMSession.SetPlayerState(player.ProfileId, RMState.None);
            st.CooldownTimer = 0f;
            if (player.IsYourPlayer)
            {
                VFX_UI.Text(Color.green, PlayerFacingMessages.PostRevive.CooldownEnded);
            }
        }

        //====================[ Revival Flow ]====================
        public static void BeginPostRevival(Player player, string playerId, RMPlayer st, bool applyFinalize)
        {
            RMSession.SetPlayerState(playerId, RMState.Revived);
            st.KillOverride = false;
            st.IsBeingRevived = false;
            st.IsSelfReviving = false;
            st.GiveUpHoldTime = -1f;
            st.CurrentReviverId = string.Empty;

            var source = (ReviveSource)st.ReviveRequestedSource;
            st.InvulnerabilityTimer = PostReviveEffects.GetInvulnDuration(source);

            // Ghost mode is host-driven (bots live on the host), so every machine clears it - by id, so
            // the flag is dropped even when this machine has no Player object for them.
            try { GhostMode.ExitGhostModeById(playerId); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[PostRevival] ExitGhostMode error: {ex.Message}"); }

            if (player == null || !player.IsYourPlayer) return;

            HeartbeatEffect.Stop(st);
            DownedScreenEffects.Stop();
            RevivePresentation.Stop(player, st, "BeginPostRevival");
            GodMode.ForceEnable(player);
            DownedUiBlocker.SetBlocked(false);

            if (applyFinalize)
            {
                try { PostReviveEffects.Apply(player, source); }
                catch (Exception ex) { Plugin.LogSource.LogError($"[PostRevival] PostReviveEffects error: {ex.Message}"); }
            }

            StartInvulnerabilityPeriod(player, st);
        }

        private static void StartInvulnerabilityPeriod(Player player, RMPlayer st)
        {
            try
            {
                ReviveDebug.Log("InvulnStart_Enter", player.ProfileId, true, $"state={st.State} hasInit={st.HasInitializedInvulnerability}");

                if (!st.HasInitializedInvulnerability)
                {
                    st.HasInitializedInvulnerability = true;

                    // Centralized restore point: release downed locks, then restore weapon and movement.
                    DownedMovementController.ReleaseProne(player);
                    DownedMovementController.ReleaseEmptyHands(player);
                    PlayerRestorations.RestorePlayerWeapon(player);
                    PlayerRestorations.RestorePlayerMovement(player, forceStandingPose: true);
                    DownedMovementController.ReattachMovementHooks(player);
                    // The invulnerability speed multiplier is applied by DownedMovementController.TickSpeedLimit.
                }

                VFX_UI.HideTransitPanel();
                st.CriticalStateMainTimer?.Stop();
                st.CriticalStateMainTimer = null;
                VFX_UI.HideObjectivePanel();
                DownedStateController.ClearRevivePromptTimer(st);
                st.RevivePromptTimer = VFX_UI.ObjectivePanel(Color.blue, PlayerFacingMessages.PostRevive.InvulnerableObjective, st.InvulnerabilityTimer);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostRevival] StartInvulnerabilityPeriod error: {ex.Message}"); }
        }

        private static void EndInvulnerability(Player player, RMPlayer st)
        {
            var source = (ReviveSource)st.ReviveRequestedSource;
            st.CurrentReviverId = string.Empty;
            st.InvulnerabilityTimer = 0f;

            float cd = PostReviveEffects.GetCooldownDuration(source);
            RMSession.SetPlayerState(player.ProfileId, RMState.CoolDown);
            st.CooldownTimer = cd;

            if (!player.IsYourPlayer) return;

            GodMode.Disable(player);
            DownedHealthAndEffectsManager.RemoveRevivableState(player);
            DownedMovementController.ReleaseProne(player);
            DownedMovementController.ReleaseEmptyHands(player);
            PlayerRestorations.RestorePlayerMovement(player, forceStandingPose: false);
            DownedMovementController.ClearSpeedLimit(player);

            RevivalAuthority.NotifyEndInvulnerability(player.ProfileId);
            FikaBridge.SendPlayerStateResetPacket(player.ProfileId, isDead: false, cd);
            st.ResyncCooldown = -1f;
            DownedStateController.ClearRevivePromptTimer(st);
            VFX_UI.HideObjectivePanel();
            VFX_UI.Text(Color.cyan, PlayerFacingMessages.PostRevive.InvulnerabilityEnded(cd));
            if (cd > 0f) PostReviveEffects.ApplyCooldownEffect(player, cd);
        }
    }
}
