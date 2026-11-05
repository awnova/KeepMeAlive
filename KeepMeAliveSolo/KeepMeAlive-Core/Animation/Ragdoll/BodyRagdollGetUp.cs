//====================[ Imports ]====================
using System;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Get Up ]====================
    // The player taking the body back: the roll onto the front and the hand-back to EFT.
    internal static partial class BodyRagdoll
    {
        //====================[ Get Up ]====================
        // The player has the body back: it rolls onto its front, turning and sliding onto where the player is, then
        // goes back to EFT's animation. A body that cannot roll (lying on its front, or nothing to play) goes back
        // straight away.
        private static void GetUp(Session session)
        {
            var player = session.Player;
            var pose = session.Pose;

            // A revive completion or death on the local player: hand back immediately unless currently in the
            // self-revive progress phase, which rolls the body onto its front while the camera stays on the head.
            if (RMSession.TryGetPlayerState(player.ProfileId, out var st) && st.State != RMState.BleedingOut
                && st.State != RMState.Reviving)
            {
                Exit(session);
                return;
            }

            // Already lying prone under EFT's own animation: nothing to roll.
            if (session.Phase == BodyPhase.Prone)
            {
                Exit(session);
                return;
            }

            DownedRagdollController.GetUpTarget(player, out Vector3 root, out float yaw);

            try
            {
                if (session.Phase == BodyPhase.Pose)
                {
                    if (!pose.BeginGetUp(root, yaw))
                        Exit(session);
                    return;
                }

                // A pose whose playback failed holds still; it will not finish.
                if (pose.GetUpDone || !pose.enabled)
                {
                    Exit(session);
                    return;
                }
                pose.SetGetUpTarget(root, yaw);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyRagdoll] Get up failed for {player.ProfileId}: {ex}");
                Exit(session);
            }
        }
    }
}
