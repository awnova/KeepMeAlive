//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Self Stim ]====================
    // A downed player stabbing themselves with a stim pen for the length of their self-revive key hold (SelfStimSequence). The owner asks
    // for it when the key goes down and every observer when the packet comes (SelfStimFlow); the body plays it as soon as it can:
    //   on its back (DownedPose)     - the sequence starts on the pose that is playing, once the fall is over;
    //   lying prone (EFT's own)      - the body is taken from EFT as a drop rest on its back does it (ProneToPose), and the sequence
    //                                  starts with the roll onto its back, placed on the player's root.
    // A body being dragged (the physics) waits for the drag to end; a request older than its own hold is dropped.
    internal static partial class BodyRagdoll
    {
        //====================[ Requests ]====================
        private sealed class StimRequest
        {
            public float Start;
            public float HoldSeconds;
            public float Yaw;
        }

        // A request the body has not taken yet is kept this long past its hold (s).
        private const float StimRequestGraceSeconds = 1f;

        private static readonly Dictionary<string, StimRequest> StimRequests = new Dictionary<string, StimRequest>();

        // The player's self-revive key went down: stab themselves for `holdSeconds`, turned to `yaw` (their body's) if they lie prone.
        public static void BeginSelfStim(Player player, float holdSeconds, float yaw)
        {
            if (!PlayerUtils.IsHuman(player) || string.IsNullOrEmpty(player.ProfileId) || FikaBridge.IsHeadless()) return;
            StimRequests[player.ProfileId] = new StimRequest { Start = Time.time, HoldSeconds = holdSeconds, Yaw = yaw };
            RevivalDebugLog.LogDebug($"[BodyRagdoll] Self-stim asked for {player.ProfileId}: hold {holdSeconds:F1}s, yaw {yaw:F0}");
        }

        // The key was let go, or the revive ended some other way: what has not started is dropped, what plays is cut short.
        public static void CancelSelfStim(Player player)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;
            StimRequests.Remove(player.ProfileId);
            if (Active.TryGetValue(player.ProfileId, out var session) && session.Pose != null)
                session.Pose.CancelSelfStim(Time.time);
        }

        private static bool TryGetStim(Player player, out StimRequest request)
        {
            if (!StimRequests.TryGetValue(player.ProfileId, out request)) return false;
            if (Time.time - request.Start < request.HoldSeconds + StimRequestGraceSeconds) return true;
            StimRequests.Remove(player.ProfileId);
            return false;
        }

        //====================[ Start ]====================
        // Per tick for a body that is animated: starts the sequence asked for, once the fall is over.
        private static void StartSelfStimOnPose(Session session)
        {
            var player = session.Player;
            var pose = session.Pose;
            if (pose == null || pose.IsPlayingFall || pose.IsGettingUp || !TryGetStim(player, out var request)) return;

            StimRequests.Remove(player.ProfileId);
            // On its back here whatever the owner's body lay as: no roll.
            var sequence = SelfStimSequence.TryCreate(request.Start, request.HoldSeconds, false, request.Yaw);
            if (sequence == null || !pose.BeginSelfStim(sequence))
            {
                Plugin.LogSource.LogWarning($"[BodyRagdoll] Self-stim could not start for {player.ProfileId}");
                return;
            }
            session.SelfStimmed = true;
        }

        // A prone body with a request: taken back from EFT (as a grab does) and given the sequence, which begins with the roll onto its
        // back, placed on the player's root.
        private static void ProneToSelfStim(Session session, StimRequest request)
        {
            var player = session.Player;
            if (InProneTransit(player)) return;
            var sequence = SelfStimSequence.TryCreate(request.Start, request.HoldSeconds, true, request.Yaw);
            if (sequence == null)
            {
                StimRequests.Remove(player.ProfileId);
                Plugin.LogSource.LogWarning($"[BodyRagdoll] Self-stim has no clips for {player.ProfileId}; the body stays prone");
                return;
            }

            try
            {
                TakeFromProne(session);

                StimRequests.Remove(player.ProfileId);
                var pool = player.GetComponent<PlayerPoolObject>();
                session.Pose = DownedPose.TryStartSelfStimFromProne(player, pool, sequence, player.Position);
                if (session.Pose != null)
                {
                    session.SelfStimmed = true;
                    RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId} rolls onto its back for the self-stim (yaw {request.Yaw:F0})");
                    return;
                }

                // Nothing to play: EFT's own prone body again.
                Plugin.LogSource.LogWarning($"[BodyRagdoll] Self-stim could not start from prone for {player.ProfileId}");
                Exit(session, ExitPose.Rest);
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Taking a prone body for the self-stim failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }
    }
}
