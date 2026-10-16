//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;
using KeepMeAlive.Components;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: State ]====================
    // What is kept about each taken-over body while it is one: the session, and the small records of what the physics
    // changed so that ending it can put every piece back.
    internal static partial class BodyRagdoll
    {
        //====================[ Session ]====================
        private sealed class Session
        {
            public ObservedPlayer Player;
            public Rigidbody Chest;
            // The upper arms; their joints are the shoulders the dragger holds the body by.
            public Rigidbody LeftShoulder;
            public Rigidbody RightShoulder;
            // The bottom of the spine, for which way the body faces.
            public Rigidbody Pelvis;

            // Restored on exit.
            public Player.EAnimatorMask EnabledAnimators;
            public bool ComponentWasEnabled;
            public bool BodyAnimatorWasEnabled;
            public bool ArmsAnimatorWasEnabled;
            public BodyInteractable Interactable;

            // Every bone's local pose before the body was taken over: what it goes back to on exit.
            public readonly List<RestBone> Rest = new List<RestBone>();

            // Animated: the pose playing (null while the physics has the body).
            public DownedPose Pose;
            public BodyPhase Phase => Pose == null ? BodyPhase.Physics : Pose.IsGettingUp ? BodyPhase.Roll : BodyPhase.Pose;
            // Debug logs only: the next time the muscles' error is logged.
            public float NextMuscleTrace;
            // Debug logs only, since the last muscle trace: how many ticks the speed and spin caps (BodyRagdollSpeed) cut something, and
            // the fastest a limb was (relative to the pelvis) and spun as the physics left it, before the cap.
            public int SpeedClamps, SpinClamps;
            public float PeakLimbSpeed, PeakLimbSpin, PeakPelvisSpeed;
            // Physics: when the body was last let go (-1 while held), and whether it stays physical until the next
            // grab (let go on its front, or no animation to go back to).
            public float FreeSince = -1f;
            public bool StayPhysical;

            public readonly List<RigidbodyState> Bodies = new List<RigidbodyState>();
            public readonly List<JointState> Joints = new List<JointState>();
            public readonly List<Detached> Tops = new List<Detached>();
            public readonly List<Collider> Contacts = new List<Collider>();
            public readonly List<Muscle> Muscles = new List<Muscle>();

            // The dragger's hands (BodyRagdollHold): the kinematic body that holds the chest, the hold it last aimed at,
            // and where it took hold (the pickup starts from there).
            public GameObject Hand;
            public Rigidbody HandBody;
            public Vector3 LastHoldTarget;
            public Vector3 PickupFrom;
            public Quaternion PickupFromRotation;

            // Roll of the held body away from lying on its back. RollArmed: it has been within MaxGripRoll since, so
            // a body still being squared up is not let go before it is.
            public bool RollArmed;
            public bool Rolled;

            // Contact colliders currently passing through the world after a snag.
            public readonly Dictionary<Collider, Release> Released = new Dictionary<Collider, Release>();
        }

        // What has a taken-over body.
        private enum BodyPhase
        {
            // DownedPose plays the fall or the breathing loop.
            Pose,
            // DownedPose plays the roll onto the front, then the body goes back to EFT.
            Roll,
            // The ragdoll simulates (dragged, or let go and not yet settled).
            Physics
        }

        // When a released contact was first let go, and the earliest it may collide again.
        private struct Release
        {
            public float Since;
            public float Until;
        }

        // What the bones do when the ragdoll ends.
        private enum ExitPose
        {
            // Ease back into the animation (the normal end).
            Blend,
            // Straight back to the pre-ragdoll pose (a simulation that went bad is not worth blending from).
            Rest,
            // Stay where the simulation left them: the player died, and the corpse ragdoll carries on from there.
            Keep
        }

        private struct RigidbodyState
        {
            public Rigidbody Body;
            public bool WasKinematic;
            public CollisionDetectionMode CollisionMode;
            public float MaxDepenetrationVelocity;
            public float SleepThreshold;
            public int SolverIterations;
            public int SolverVelocityIterations;
            public float MaxAngularVelocity;
            public EBodyPartColliderType Part;
        }

        private struct RestBone
        {
            public Transform Bone;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private struct JointState
        {
            public CharacterJoint Joint;
            // The body the joint sits on (the child bone; connectedBody is its parent).
            public Rigidbody Body;
            public bool EnablePreprocessing;
            public float MassScale;
            public float ConnectedMassScale;
            public bool EnableProjection;
        }

        // Holds a body toward its neutral rotation relative to the body its joint hangs from.
        private struct Muscle
        {
            public Rigidbody Body;
            public Rigidbody Parent;
            public Quaternion TargetRelative;
            // The joint (on Body) and everything it moves (Body and all that hangs off it), for the inertia about
            // the joint.
            public CharacterJoint Joint;
            public Rigidbody[] Load;
            // An arm: its muscle carries its own weight (HoldWeight). The legs and the torso lie on the floor in the
            // muscle pose, which holds them; carrying their weight as well would lift them off it.
            public bool Arm;
        }

        // A ragdoll root taken out from under the player while it simulates.
        private struct Detached
        {
            public Transform Transform;
            public Transform Parent;
            public int SiblingIndex;
        }
    }
}
