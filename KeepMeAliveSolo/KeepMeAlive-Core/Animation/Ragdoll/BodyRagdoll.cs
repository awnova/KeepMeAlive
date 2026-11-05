//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll ]====================
    // Drives the local player's downed body pose while limp:
    //   animated - DownedPose plays the fall, then the breathing loop, and the self-stim sequence;
    //   prone    - when downed while already prone, stays in EFT's prone animation unless self-stimming;
    // When the player moves or revives, it rolls onto its front (DownedPose.BeginGetUp) and blends back to EFT.
    internal static partial class BodyRagdoll
    {
        //====================[ Tuning ]====================
        // Ragdoll-to-animation blend on exit (seconds).
        private const float BlendOutDuration = 0.35f;

        private static readonly Dictionary<string, Session> Active = new Dictionary<string, Session>();
        // Players whose pose could not be built or went bad; not retried until it is no longer wanted.
        private static readonly HashSet<string> Failed = new HashSet<string>();

        private static void MarkFailed(string profileId) => Failed.Add(profileId);

        //====================[ Public API ]====================
        // Updates the downed pose for the local player.
        public static void Sync(Player player, bool want)
        {
            if (player == null || !player.IsYourPlayer || string.IsNullOrEmpty(player.ProfileId)) return;

            string id = player.ProfileId;
            bool active = Active.TryGetValue(id, out var session);

            if (active && session.Player == null)
            {
                Active.Remove(id);
                active = false;
            }

            // The game squashes the local Ribcage (and the arms under it) along the view's depth in first person;
            // held at full scale for as long as the body pose is active (released in Exit).
            if (active && session.Player != null) FullScaleArms.Want(session.Player, session, !session.Prone);

            if (!want)
            {
                Failed.Remove(id);
                StimRequests.Remove(id);
            }

            if (want && !active)
            {
                if (!Failed.Contains(id)) Enter(player);
            }
            else if (want && session.Phase == BodyPhase.Prone)
            {
                if (TryGetStim(player, out var stim)) ProneToSelfStim(session, stim);
            }
            else if (want && session.Phase == BodyPhase.Pose)
            {
                StartSelfStimOnPose(session);
            }
            else if (!want && active)
            {
                GetUp(session);
            }
        }

        // The local player is still going down into prone.
        private static bool InProneTransit(Player player) =>
            player.CurrentStateName == EPlayerState.Transit2Prone;

        public static void ReleaseForDeath(Player player) => EndForPlayer(player, ExitPose.Keep);

        private static void OnDeadOrUnspawn(Player player) => EndForPlayer(player, ExitPose.Rest);

        private static void EndForPlayer(Player player, ExitPose pose)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;

            if (Active.TryGetValue(player.ProfileId, out var session))
                Exit(session, pose);
            Failed.Remove(player.ProfileId);

            DestroyBlendOut(player);
        }

        private static void DestroyBlendOut(Player player)
        {
            var blend = player != null ? player.GetComponent<BlendOut>() : null;
            if (blend != null)
            {
                // Disabled as well: Destroy only takes effect at the end of the frame.
                blend.enabled = false;
                UnityEngine.Object.Destroy(blend);
            }
        }

        // Raid boundary cleanup.
        public static void ClearAll()
        {
            var sessions = new List<Session>(Active.Values);
            foreach (var session in sessions)
            {
                try
                {
                    if (session.Player != null)
                        Exit(session, ExitPose.Rest);
                }
                catch (Exception ex) { Plugin.LogSource.LogWarning($"[BodyRagdoll] Session cleanup failed: {ex.Message}"); }
            }
            Active.Clear();
            Failed.Clear();
            StimRequests.Clear();
            ClipRig.ClearReports();
            SkeletonBind.ClearKnown();
        }

        //====================[ Enter ]====================
        private static void Enter(Player player)
        {
            if (!PlayerUtils.IsLocalHuman(player)) return;
            var pool = player.GetComponent<PlayerPoolObject>();
            if (pool == null) return;

            DestroyBlendOut(player);

            var session = new Session { Player = player };
            Active[player.ProfileId] = session;
            player.OnPlayerDeadOrUnspawn += OnDeadOrUnspawn;

            try
            {
                // Fall back to ordinary EFT animation when the skeleton cannot play the baked clips.
                if (!DownedPose.CanBuildRig(player))
                {
                    MarkFailed(player.ProfileId);
                    Exit(session, ExitPose.Rest);
                    return;
                }

                // Downed while already prone (no recorded fall): EFT's prone body is right as it is.
                if (DownedRagdollController.LocalFallOf(player) == null && !DownedPose.IsUpright(player))
                {
                    session.Prone = true;
                    return;
                }

                CaptureRest(player, session);
                session.Pose = DownedPose.TryStartLocalFall(player, pool);
                if (session.Pose != null) return;

                session.Prone = true;
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Enter failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        //====================[ Exit ]====================
        // The two upper arm bones of the rig, the roots of everything the arms pose.
        private static List<Transform> ArmRoots(Session session)
        {
            var roots = new List<Transform>();
            foreach (var rest in session.Rest)
                if (rest.Bone != null && (rest.Bone.name == BoneNames.LeftUpperArm || rest.Bone.name == BoneNames.RightUpperArm))
                    roots.Add(rest.Bone);
            return roots;
        }

        private static bool IsUnder(Transform bone, List<Transform> roots)
        {
            foreach (var root in roots)
                if (bone.IsChildOf(root)) return true;
            return false;
        }

        private static void Exit(Session session, ExitPose pose = ExitPose.Blend)
        {
            var player = session.Player;
            Active.Remove(player.ProfileId);
            player.OnPlayerDeadOrUnspawn -= OnDeadOrUnspawn;
            FullScaleArms.Want(player, session, false);

            // Lying prone under EFT's animation: nothing was taken over.
            if (session.Prone) return;
            HandBack(session, pose);
        }

        private static void HandBack(Session session, ExitPose pose)
        {
            var player = session.Player;
            try
            {
                if (session.Pose != null)
                {
                    session.Pose.Stop();
                    session.Pose = null;
                }

                if (pose == ExitPose.Blend)
                {
                    var blend = player.gameObject.AddComponent<BlendOut>();
                    var arms = ArmRoots(session);
                    foreach (var rest in session.Rest)
                    {
                        if (rest.Bone == null) continue;
                        // The local arms are the first person arms: whatever the game or the revive hands pose there is
                        // theirs from the first frame, not eased in from the downed pose.
                        if (arms != null && IsUnder(rest.Bone, arms)) continue;
                        blend.Capture(rest.Bone);
                    }
                    blend.Trace = player.PlayerBones.Pelvis.Original;
                    var bodyAnimator = player.BodyAnimatorCommon;
                    blend.AnimatorOn = () => bodyAnimator.enabled;
                }
                else if (pose == ExitPose.Rest)
                {
                    foreach (var rest in session.Rest)
                    {
                        if (rest.Bone == null) continue;
                        rest.Bone.localPosition = rest.LocalPosition;
                        rest.Bone.localRotation = rest.LocalRotation;
                    }
                }
                // Keep: the corpse ragdoll carries on from the pose as it is.

                RevivalDebugLog.LogDebug($"[BodyRagdoll] Exited for {player.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyRagdoll] Exit failed: {ex}");
            }
        }
    }
}
