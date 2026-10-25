//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ SelfReviveInput ]====================
    // Local self-revive hold: KeyDown gates -> hold to threshold -> hand off to the shared
    // authorize/consume/start pipeline in RevivalController. Releasing before the threshold cancels.
    internal static class SelfReviveInput
    {
        //====================[ Per-Frame Tick ]====================
        public static void Tick(Player player, RMPlayer st)
        {
            if (!RevivePolicy.IsSelfReviveEnabled) return;
            if (st.State != RMState.BleedingOut) return;
            // Self-revive is off while spectating; the player returns to their body first.
            if (DownedSpectate.IsActive) return;
            if (!string.IsNullOrEmpty(st.CurrentReviverId) && st.CurrentReviverId != player.ProfileId) return;

            // Hold completed and the server is authorizing: the attempt is committed. A new KeyDown
            // here would start a second attempt and abandon this one (after its item was consumed).
            if (st.SelfReviveAwaitingAuth) return;

            KeyCode key = KeepMeAliveSettings.SELF_REVIVAL_KEY.Value;
            float holdDuration = RevivePolicy.GetHoldDuration(ReviveSource.Self);

            if (Input.GetKeyDown(key))
            {
                BeginHold(player, st, key, holdDuration);
                return;
            }

            if (!st.IsSelfReviving) return;

            // Poll the held state rather than waiting for KeyUp: a sub-frame tap or a focus loss
            // never delivers a usable KeyUp, and a hold left open blocks bleed-out and give-up.
            if (!Input.GetKey(key))
            {
                RevivalController.Trace("SelfHold_ReleasedCanceled", player, st, $"| key={key}");
                st.ClearSelfReviveInput();
                DownedStateController.CancelReviveState(player, st, null);
                return;
            }

            st.SelfReviveHoldTime += Time.deltaTime;
            if (st.SelfReviveHoldTime >= holdDuration)
            {
                st.SelfReviveAwaitingAuth = true;
                RevivalController.Trace("SelfHold_Completed", player, st, "| threshold reached; begin auth");
                RevivalController.BeginSelfReviveStart(player, st);
            }
        }

        private static void BeginHold(Player player, RMPlayer st, KeyCode key, float holdDuration)
        {
            RevivalController.Trace("SelfHold_KeyDown", player, st, $"| key={key}");

            if (!RevivePolicy.HasRequiredItem(ReviveSource.Self, player))
            {
                // Said by the strip's "Needs stim" chip; refreshed here, as the stim may have been taken since it was drawn.
                RevivalController.Trace("SelfHold_BlockedNoStim", player, st);
                DownedStateController.ShowSelfRevivePromptIfEligible(player);
                return;
            }

            int selfReviveCost = RevivePolicy.LivesCost(ReviveSource.Self);
            if (st.LivesRemaining < selfReviveCost)
            {
                // Said by the strip's "Not enough lives" chip; refreshed here in case the lives changed since it was drawn.
                RevivalController.Trace("SelfHold_BlockedOutOfLives", player, st, $"| livesRemaining={st.LivesRemaining} cost={selfReviveCost}");
                DownedStateController.ShowSelfRevivePromptIfEligible(player);
                return;
            }

            st.SelfReviveAttemptId++;
            st.ClearSelfReviveInput();
            st.IsSelfReviving = true;
            RevivalController.Trace("SelfHold_Started", player, st, $"| holdTarget={holdDuration:F2}s");

            Hud.HideHold();
            st.RevivePromptTimer = Hud.ShowHold(Hud.HoldKind.SelfRevive, holdDuration);

            // The body stabs itself with a stim pen for as long as the key is held (SelfStimSequence), on every peer.
            SelfStimFlow.Begin(player, holdDuration);
        }
    }
}
