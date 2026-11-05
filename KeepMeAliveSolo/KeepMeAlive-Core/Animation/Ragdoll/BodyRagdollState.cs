//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: State ]====================
    // Session state for the local player's downed body pose and its pre-downed rest bones.
    internal static partial class BodyRagdoll
    {
        //====================[ Session ]====================
        private sealed class Session
        {
            public Player Player;

            // Every bone's local pose before the body was taken over: what it goes back to on exit.
            public readonly List<RestBone> Rest = new List<RestBone>();

            // Animated: the pose playing.
            public DownedPose Pose;
            public BodyPhase Phase => Prone ? BodyPhase.Prone : Pose.IsGettingUp ? BodyPhase.Roll : BodyPhase.Pose;
            // The body lies on its front and EFT's own prone animation has it (nothing of it is taken over; the session only
            // remembers that, so the body is not taken again next tick).
            public bool Prone;
        }

        // What has a taken-over body.
        private enum BodyPhase
        {
            // DownedPose plays the fall or the breathing loop.
            Pose,
            // DownedPose plays the roll onto the front, then the body goes back to EFT.
            Roll,
            // Lying on its front, handed to EFT's prone animation.
            Prone
        }

        // What the bones do when the pose ends.
        private enum ExitPose
        {
            // Ease back into the animation (the normal end).
            Blend,
            // Straight back to the pre-downed pose.
            Rest,
            // Stay where the pose left them: the player died, and the corpse ragdoll carries on from there.
            Keep
        }

        private struct RestBone
        {
            public Transform Bone;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }
    }
}
