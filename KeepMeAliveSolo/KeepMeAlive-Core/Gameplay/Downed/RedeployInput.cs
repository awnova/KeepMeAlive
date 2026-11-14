//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ RedeployInput ]====================
    // Local redeploy hold: KeyDown -> hold to the threshold -> hand off to RedeployController. Releasing early cancels.
    // It shares the downed state with self-revive and give-up, so it only runs while neither of them has the player.
    internal static class RedeployInput
    {
        //====================[ Per-Frame Tick ]====================
        public static void Tick(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;

            // Hold completed and the controller is authorizing / swapping: the attempt is committed.
            if (st.RedeployAwaitingAuth) return;

            if (!Available(player, st))
            {
                if (st.IsRedeployHolding) CancelHold(player, st);
                return;
            }

            KeyCode key = KeepMeAliveSettings.REDEPLOY_KEY.Value;

            if (!st.IsRedeployHolding)
            {
                if (Input.GetKeyDown(key)) BeginHold(player, st);
                return;
            }

            // Polled rather than waiting for KeyUp: a sub-frame tap or a focus loss never delivers a usable KeyUp, and a
            // hold left open would block bleed-out and give-up.
            if (!Input.GetKey(key))
            {
                RevivalController.Trace("RedeployHold_Released", player, st, $"| key={key}");
                CancelHold(player, st);
                return;
            }

            st.RedeployHoldTime += Time.deltaTime;
            if (st.RedeployHoldTime < RevivePolicy.GetHoldDuration(ReviveSource.Redeploy)) return;

            RevivalController.Trace("RedeployHold_Completed", player, st, "| threshold reached; begin redeploy");
            st.IsRedeployHolding = false;
            st.RedeployHoldTime = 0f;
            st.RedeployAwaitingAuth = true;
            Hud.HideHold();
            DownedStateController.ClearRevivePromptTimer(st);
            RedeployController.Begin(player, st);
        }

        // Whether redeploy may be started or kept going right now.
        private static bool Available(Player player, RMPlayer st)
        {
            if (!RevivePolicy.IsEnabled(ReviveSource.Redeploy)) return false;
            if (st.State != RMState.BleedingOut) return false;
            if (st.IsSelfReviving || st.SelfReviveAwaitingAuth) return false;
            return true;
        }

        private static void BeginHold(Player player, RMPlayer st)
        {
            if (!RevivePolicy.HasLivesFor(ReviveSource.Redeploy, st.LivesRemaining))
            {
                // Said by the strip's "Not enough lives" chip; refreshed in case the lives changed since it was drawn.
                RevivalController.Trace("RedeployHold_BlockedOutOfLives", player, st, $"| livesRemaining={st.LivesRemaining}");
                DownedStateController.ShowSelfRevivePromptIfEligible(player);
                return;
            }

            st.RedeployAttemptId++;
            st.ClearRedeployInput();
            st.IsRedeployHolding = true;
            st.GiveUpHoldTime = -1f;
            RevivalController.Trace("RedeployHold_Started", player, st);

            float holdDuration = RevivePolicy.GetHoldDuration(ReviveSource.Redeploy);
            Hud.HideHold();
            DownedStateController.ClearRevivePromptTimer(st);
            st.RevivePromptTimer = Hud.ShowHold(Hud.HoldKind.Redeploy, holdDuration);
        }

        private static void CancelHold(Player player, RMPlayer st)
        {
            st.ClearRedeployInput();
            Hud.HideHold();
            DownedStateController.ClearRevivePromptTimer(st);
            DownedStateController.ShowSelfRevivePromptIfEligible(player);
        }

        //====================[ Strip ]====================
        // The redeploy item on the downed strip: its cost, or that the lives can't pay for it. Hidden when it is off.
        public static void RefreshStrip(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;

            if (!RevivePolicy.IsEnabled(ReviveSource.Redeploy))
            {
                Hud.SetRedeployStrip(Hud.RedeployStrip.Hidden);
                return;
            }

            var st = RMSession.GetPlayerState(player.ProfileId);
            if (!RevivePolicy.HasLivesFor(ReviveSource.Redeploy, st.LivesRemaining))
            {
                Hud.SetRedeployStrip(Hud.RedeployStrip.NoLives, RevivePolicy.LivesCost(ReviveSource.Redeploy));
            }
            else
            {
                int cost = RevivePolicy.LivesSpent(ReviveSource.Redeploy, st.LivesRemaining);
                Hud.SetRedeployStrip(Hud.RedeployStrip.Available, cost);
            }
        }
    }
}
