//====================[ Imports ]====================
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ SelfReviveInput ]====================
    // Local self-revive hold: KeyDown gates -> hold to threshold -> hand off to the shared
    // authorize/consume/start pipeline in RevivalController. Releasing before the threshold cancels.
    internal static class SelfReviveInput
    {
        //====================[ Per-Frame Tick ]====================
        public static void Tick(Player player, RMPlayer st)
        {
            if (!RevivePolicy.IsEnabled(ReviveSource.Self)) return;
            if (st.State != RMState.BleedingOut) return;
            if (!string.IsNullOrEmpty(st.CurrentReviverId) && st.CurrentReviverId != player.ProfileId) return;

            KeyCode key = KeepMeAliveSettings.SELF_REVIVAL_KEY.Value;
            float holdDuration = RevivePolicy.GetHoldDuration(ReviveSource.Self);

            if (Input.GetKeyDown(key))
            {
                BeginHold(player, st, key, holdDuration);
            }
            else if (Input.GetKey(key) && st.IsSelfReviving)
            {
                if (st.SelfReviveAwaitingAuth) return;

                st.SelfReviveHoldTime += Time.deltaTime;
                if (st.SelfReviveHoldTime >= holdDuration)
                {
                    st.SelfReviveAwaitingAuth = true;
                    RevivalController.Trace("SelfHold_Completed", player, st, "| threshold reached; begin auth");
                    RevivalController.BeginSelfReviveStart(player, st);
                }
            }
            else if (Input.GetKeyUp(key) && st.IsSelfReviving)
            {
                if (st.SelfReviveAwaitingAuth)
                {
                    RevivalController.Trace("SelfHold_KeyUpIgnoredAfterCommit", player, st, $"| key={key}");
                    return;
                }

                if (st.SelfReviveHoldTime > 0f)
                {
                    RevivalController.Trace("SelfHold_KeyUpCanceled", player, st, $"| key={key}");
                    st.ClearSelfReviveInput();
                    DownedStateController.CancelReviveState(player, st, PlayerFacingMessages.Revive.SelfReviveCanceled, Color.yellow);
                }
            }
        }

        private static void BeginHold(Player player, RMPlayer st, KeyCode key, float holdDuration)
        {
            RevivalController.Trace("SelfHold_KeyDown", player, st, $"| key={key}");

            if (RevivePolicy.RequiresItem && !ModUtils.HasReviveItem(player))
            {
                RevivalController.Trace("SelfHold_BlockedNoReviveItem", player, st);
                VFX_UI.Text(Color.red, PlayerFacingMessages.Revive.NoReviveItemFound);
                return;
            }

            int selfReviveCost = SyncedServerConfigStore.Config.Gameplay.Revival.SelfReviveLivesCost;
            if (st.LivesRemaining < selfReviveCost)
            {
                RevivalController.Trace("SelfHold_BlockedOutOfLives", player, st, $"| livesRemaining={st.LivesRemaining} cost={selfReviveCost}");
                VFX_UI.Text(Color.red, PlayerFacingMessages.Revive.NotEnoughLives(selfReviveCost));
                return;
            }

            st.SelfReviveAttemptId++;
            st.ClearSelfReviveInput();
            st.IsSelfReviving = true;
            RevivalController.Trace("SelfHold_Started", player, st, $"| holdTarget={holdDuration:F2}s");

            VFX_UI.HideObjectivePanel();
            st.RevivePromptTimer = VFX_UI.ObjectivePanel(VFX_UI.Gradient(Color.blue, Color.green), PlayerFacingMessages.Revive.HoldObjective, holdDuration);
        }
    }
}
