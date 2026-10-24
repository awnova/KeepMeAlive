//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: State ]====================
    // What is kept about each taken-over body while it is one: the session, and the small records of what the physics
    // changed so that ending it can put every piece back.
    internal static partial class BodyRagdoll
    {
        //====================[ Session ]====================
        private sealed class Session
        {
            public Player Player;
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
            // The local body only: the body animation is off while the physics runs (SuspendLocalAnimation).
            public bool LocalSuspended;
            public Player.EAnimatorMask LocalMaskWas;
            public bool LocalBodyAnimatorWas;
            public bool LocalKeepStateWas;
            public bool LocalIgnoreDeltaWas;
            // The local body's view from the head while it is a ragdoll.
            public LocalBodyCamera View;

            // Every bone's local pose before the body was taken over: what it goes back to on exit.
            public readonly List<RestBone> Rest = new List<RestBone>();

            // Animated: the pose playing (null while the physics has the body).
            public DownedPose Pose;
            public BodyPhase Phase => Prone ? BodyPhase.Prone : Pose == null ? BodyPhase.Physics : Pose.IsGettingUp ? BodyPhase.Roll : BodyPhase.Pose;
            // The body lies on its front and EFT's own prone animation has it (nothing of it is taken over; the session only
            // remembers that, so the body is not taken again next tick).
            public bool Prone;
            // The body has played the self-revive sequence (BodyRagdollSelfStim): it lies on its back where it is, whatever a drag once said.
            public bool SelfStimmed;
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
            public readonly Dictionary<EBodyPartColliderType, Rigidbody> BodyByPart = new Dictionary<EBodyPartColliderType, Rigidbody>();
            public readonly List<JointState> Joints = new List<JointState>();
            public readonly List<Detached> Tops = new List<Detached>();
            public readonly List<Collider> Contacts = new List<Collider>();
            // Limb spacers (BodyRagdollRig.AddLimbSpacers): colliders that keep the legs out of each other and the arms out of the
            // torso; removed with the physics.
            public readonly List<Collider> Spacers = new List<Collider>();
            public readonly List<Muscle> Muscles = new List<Muscle>();
            // The muscle motor on each jointed body (BodyRagdollMuscles), made with the joints and removed with the physics.
            public readonly Dictionary<Rigidbody, ConfigurableJoint> Motors = new Dictionary<Rigidbody, ConfigurableJoint>();
            public readonly List<NonPhysicalBone> NonPhysicalBones = new List<NonPhysicalBone>();

            // The dragger's hands (BodyRagdollHold): the kinematic body that holds the chest, the hold it last aimed at,
            // and where it took hold (the pickup starts from there).
            public GameObject Hand;
            public Rigidbody HandBody;
            public ConfigurableJoint HandAngularJoint;
            public Vector3 LastHoldTarget;
            public Vector3 PickupFrom;
            public Quaternion PickupFromRotation;
            // What the dragger's and the held body's overlap managers (PlayerOverlapManager) were told to ignore while the body is held,
            // to undo it. A POM pushes its player out of Player-layer colliders by their head and camera colliders.
            public readonly List<(PlayerOverlapManager Pom, Collider Other)> PomIgnored = new List<(PlayerOverlapManager Pom, Collider Other)>();
            public int IgnoreTries;

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
            Physics,
            // Lying on its front, handed to EFT's prone animation; a grab takes it back into the physics.
            Prone
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
            public float WasMass;
            public float WasDrag;
            public float WasAngularDrag;
            public CollisionDetectionMode CollisionMode;
            public float MaxDepenetrationVelocity;
            public float SleepThreshold;
            public int SolverIterations;
            public int SolverVelocityIterations;
            public float MaxAngularVelocity;
            public float MaxLinearVelocity;
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
            public Vector3 Axis;
            public Vector3 SwingAxis;
            public SoftJointLimit LowTwistLimit;
            public SoftJointLimit HighTwistLimit;
            public SoftJointLimit Swing1Limit;
            public SoftJointLimit Swing2Limit;
            public SoftJointLimitSpring TwistLimitSpring;
            public SoftJointLimitSpring SwingLimitSpring;
        }

        // A ragdoll joint's muscle: the motor (BodyRagdollMuscles) that pulls the body on its parent toward the muscle pose.
        private sealed class Muscle
        {
            public Rigidbody Body;
            public Rigidbody Parent;
            // EFT's joint (the limits); the motor sits beside it on the same pair.
            public CharacterJoint Joint;
            public ConfigurableJoint Drive;
            // Relative rotation (Inverse(Parent.rotation) * Body.rotation) in the muscle pose: the motor's zero and its target.
            public Quaternion SettleRelative;
            public MuscleGroup Group;
            public EBodyPartColliderType Part;
            // The joint's reduced swing inertia (kg m^2) that turns the strength setting (1/s^2) into the drive's torque.
            public float Inertia;
            // The most torque gravity puts on the joint (N*m, everything it moves at its lever); scales the postural tone.
            public float Weight;
            // The strength and damping ratio last put on the drive (-1 until the first).
            public float AppliedStrength = -1f;
            public float AppliedDamping = -1f;
        }

        // A leaf bone without its own Rigidbody (feet, forearm twists, palms), held at its muscle pose local rotation.
        private struct NonPhysicalBone
        {
            public Transform Bone;
            public Quaternion Local;
        }

        private enum MuscleGroup
        {
            Arms,
            Legs,
            Spine,
            NeckHead
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
