//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Get Up ]====================
    // The player taking the body back: the roll onto the front, the root turned to their yaw, and the hand-back to EFT.
    internal static partial class BodyRagdoll
    {
        //====================[ Get Up ]====================
        // The player has the body back: it rolls onto its front, turning and sliding onto where the player is, then
        // goes back to EFT's animation. A body that cannot roll (lying on its front, or nothing to play) goes back
        // straight away. Either way the root is turned to the owner's body yaw first: EFT draws the body from it once
        // it has it back, and it has not turned since the body was taken (see TurnRoot).
        private static void GetUp(Session session)
        {
            var player = session.Player;
            var pose = session.Pose;

            // A revive (or the end of one) on the local player: the revive poses the first person arms (the defibrillator,
            // the item in hand, the inventory pose), which the roll would write over for its whole length. The body goes
            // back to the game at once; a limp ended by crawling still rolls. Not a self revive, which has played the self-stim
            // on the body and rolls it onto its front for the revive's progress, the camera on the head as for the rest of the
            // downed body; its arms are the body's (SelfStimHands).
            if (player.IsYourPlayer && RMSession.TryGetPlayerState(player.ProfileId, out var st) && st.State != RMState.BleedingOut
                && !(st.State == RMState.Reviving && (ReviveSource)st.ReviveRequestedSource == ReviveSource.Self))
            {
                Exit(session);
                return;
            }

            // Already lying prone under EFT's own animation: nothing to roll, and EFT turns the root itself.
            if (session.Phase == BodyPhase.Prone)
            {
                Exit(session);
                return;
            }

            DownedRagdollController.GetUpTarget(player, out Vector3 root, out float yaw);

            try
            {
                if (session.Phase == BodyPhase.Physics)
                {
                    // Physics: on its back the roll starts from where it lies.
                    if (!DragGrip.IsOnBack(player))
                    {
                        // Back under the player (the ragdoll's top bones are detached, and TurnRoot turns the skeleton's), then
                        // turned, before Exit, whose blend starts from the bones' poses relative to the root.
                        StopPhysics(session, true);
                        TurnRoot(player, yaw);
                        Exit(session);
                        return;
                    }
                    StopPhysics(session, true);
                    TurnRoot(player, yaw);
                    session.Pose = DownedPose.TryStartGetUp(player, player.GetComponent<PlayerPoolObject>(), root, yaw);
                    if (session.Pose == null) Exit(session);
                    else WarmAnimator(session);
                    return;
                }

                TurnRoot(player, yaw);
                if (session.Phase == BodyPhase.Pose)
                {
                    if (pose.BeginGetUp(root, yaw)) WarmAnimator(session);
                    else Exit(session);
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

        // Lets the body animator run again for the length of the roll, under the roll's own pose (DownedPose writes
        // every bone it drives after the animator, so none of it shows). The animator was switched off when the body
        // went down, before it had played the move into prone that the owner made then; Fika has kept setting its
        // parameters since, so handed back cold it would play that move from its old upright state: the body pops up
        // and drops back to prone. Running through the roll it plays the move out of sight and is already in prone
        // when the body is handed back. Exit puts the animator back as it found it.
        private static void WarmAnimator(Session session)
        {
            var animator = session.Player.BodyAnimatorCommon;
            if (session.BodyAnimatorWasEnabled && animator != null) animator.enabled = true;
        }

        // Scratch for TurnRoot: the skeleton's top bones and their world poses.
        private struct KeptBone
        {
            public Transform Bone;
            public Vector3 Position;
            public Quaternion Rotation;
        }
        private static readonly List<KeptBone> _keptBones = new List<KeptBone>();

        // Turns the player root to the owner's body yaw, leaving the body where it is on screen. Fika replicates an
        // observed player's position and look but not the yaw of their body: EFT turns the root itself, from the
        // movement states the body animator runs (OnStateMove). With that animator off since the body was taken, the
        // root still faces the way it did before they went down. Handed back like that, EFT would draw the prone body
        // there and only swing it round once the player stands.
        private static void TurnRoot(Player player, float yaw)
        {
            // The local root is turned by the player's own look, and is already the body's yaw.
            if (player.IsYourPlayer) return;
            var mc = player.MovementContext;
            float delta = Mathf.DeltaAngle(mc.TransformRotation.eulerAngles.y, yaw);
            if (Mathf.Abs(delta) < 0.1f) return;

            // The skeleton hangs under the root: its top bones keep their place in the world through the turn.
            Transform skeleton = player.PlayerBones.Pelvis.Original.parent;
            _keptBones.Clear();
            foreach (Transform bone in skeleton)
                _keptBones.Add(new KeptBone { Bone = bone, Position = bone.position, Rotation = bone.rotation });

            mc.ApplyRotation(Quaternion.AngleAxis(delta, Vector3.up) * mc.TransformRotation);

            foreach (var kept in _keptBones)
                kept.Bone.SetPositionAndRotation(kept.Position, kept.Rotation);
            _keptBones.Clear();
        }
    }
}
