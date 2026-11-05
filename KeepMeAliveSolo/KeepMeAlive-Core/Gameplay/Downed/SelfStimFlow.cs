//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ SelfStimFlow ]====================
    // The owner's side of the self-stim: when the self-revive key hold starts the body plays the sequence (BodyRagdoll),
    // and when the hold ends early the same. The hold completing needs nothing here: the sequence is as long as the hold,
    // and the revive's own start takes the body back for the roll onto the front.
    internal static class SelfStimFlow
    {
        // The local player's key went down for a hold of this length.
        public static void Begin(Player player, float holdSeconds)
        {
            if (player == null || !player.IsYourPlayer) return;
            float yaw = player.Transform.rotation.eulerAngles.y;
            MakeLimp(player.ProfileId);
            BodyRagdoll.BeginSelfStim(player, holdSeconds, yaw);
        }

        // The body lies limp for the hold (a player who crawled had ended the limp).
        public static void MakeLimp(string playerId)
        {
            var st = RMSession.GetPlayerState(playerId);
            if (st == null || st.State != RMState.BleedingOut) return;
            st.IsLimp = true;
            st.LimpStartTime = Time.time;
        }

        // The local player's hold ended early.
        public static void Cancel(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            BodyRagdoll.CancelSelfStim(player);
        }
    }
}
