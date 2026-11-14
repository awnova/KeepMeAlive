//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ PostRevivalController ]====================
    // Post-revival invulnerability and cooldown lifecycle for the local player.
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
            st.IsSelfReviving = false;
            st.GiveUpHoldTime = -1f;

            st.InvulnerabilityTimer = PostReviveEffects.GetInvulnDuration((ReviveSource)st.ReviveRequestedSource);

            try { GhostMode.ExitGhostMode(player); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[PostRevival] ExitGhostMode error: {ex.Message}"); }

            if (player == null || !player.IsYourPlayer) return;

            HeartbeatEffect.Stop(st);
            DownedScreenEffects.Stop();
            RevivePresentation.Stop(player, st, "BeginPostRevival");
            GodMode.ForceEnable(player);
            Hud.SetInputBlocked(false);

            if (applyFinalize)
            {
                try { PostReviveEffects.Apply(player, (ReviveSource)st.ReviveRequestedSource); }
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

                    // Centralized restore point: release downed locks, then restore weapon and movement. Each step on its own.
                    RestoreStep("ReleaseProne", () => DownedMovementController.ReleaseProne(player));
                    RestoreStep("ReleaseEmptyHands", () => DownedMovementController.ReleaseEmptyHands(player));
                    RestoreStep("RestorePlayerWeapon", () => PlayerRestorations.RestorePlayerWeapon(player));
                    RestoreStep("RestorePlayerMovement", () => PlayerRestorations.RestorePlayerMovement(player));
                    RestoreStep("ReattachMovementHooks", () => DownedMovementController.ReattachMovementHooks(player));
                    // The invulnerability speed multiplier is applied by DownedMovementController.TickSpeedLimit.
                }

                Hud.HideAll();
                st.CriticalStateMainTimer?.Stop();
                st.CriticalStateMainTimer = null;
                st.ReviveStatusTimer?.Stop();
                st.ReviveStatusTimer = null;
                DownedStateController.ClearRevivePromptTimer(st);
                st.RevivePromptTimer = Hud.ShowStatus(Hud.StatusKind.Invulnerable, st.InvulnerabilityTimer);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostRevival] StartInvulnerabilityPeriod error: {ex.Message}"); }
        }

        private static void RestoreStep(string name, Action step)
        {
            try { step(); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostRevival] {name} error: {ex}"); }
        }

        private static void EndInvulnerability(Player player, RMPlayer st)
        {
            st.InvulnerabilityTimer = 0f;

            float cd = PostReviveEffects.GetCooldownDuration((ReviveSource)st.ReviveRequestedSource);
            RMSession.SetPlayerState(player.ProfileId, RMState.CoolDown);
            st.CooldownTimer = cd;

            if (!player.IsYourPlayer) return;

            GodMode.Disable(player);
            DownedMovementController.ReleaseProne(player);
            DownedMovementController.ReleaseEmptyHands(player);
            DownedMovementController.ClearSpeedLimit(player);
            // Recover the hands if still stuck.
            PlayerRestorations.VerifyHands(player, "invulnerability ended");

            RevivalAuthority.NotifyEndInvulnerability(player.ProfileId);
            DownedStateController.ClearRevivePromptTimer(st);
            Hud.HideStatus();
            VFX_UI.Text(Color.cyan, PlayerFacingMessages.PostRevive.InvulnerabilityEnded(cd));
            if (cd > 0f) PostReviveEffects.ApplyCooldownEffect(player, cd);
        }
    }
}
