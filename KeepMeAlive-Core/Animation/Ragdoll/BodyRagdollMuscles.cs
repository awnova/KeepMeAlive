//====================[ Imports ]====================
using System.Collections.Generic;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    // Each ragdoll joint uses a ConfigurableJoint drive toward the muscle pose. Drive strength is scaled by joint inertia so
    //====================[ BodyRagdoll: Muscles ]====================
    // different body parts respond consistently; posture tone helps support the head, back, and arms under gravity.
    internal static partial class BodyRagdoll
    {
        // Postural tone supports the neck, spine, and arms under gravity. The legs stay relaxed so they can trail on the ground.
        //====================[ Tuning ]====================
        private const float NeckPostureTone = 8f;
        private const float SpinePostureTone = 5f;
        private const float ArmPostureTone = 6f;
        // The knees only (not the hips): keeps each leg straight and braced like a living person's without lifting the leg's
        // weight off the heels (a calf lying on the floor puts almost no load on the knee, so this adds little stiffness there).
        private const float KneePostureTone = 6f;
        // Hip flexion stays soft so the legs can rest on the ground while sideways and rolling motion remain supported.
        private const float HipFlexionShare = 0.15f;
        // Hip tone resists sideways and rolling motion without lifting the legs off the ground.
        private const float HipSideTone = 6f;
        // The hip joint's flexion axis in the thigh's own frame (EFT's CharacterJoint axis on the thighs).
        private static readonly Vector3 HipFlexionAxis = Vector3.forward;

        // Leaf bones without a rigidbody, posed to the muscle pose alongside the physical joints (ankles, forearm twists, wrists).
        // Bones that sit above a rigidbody (Spine1, Ribcage, Collarbones, Neck, ThighTwist) are never written while the physics
        // runs: turning a parent above a dynamic body fights PhysX's transform writeback and the joint anchors.
        private static readonly string[] PosedHelperBones =
        {
            BoneNames.LeftFoot,
            BoneNames.RightFoot,
            BoneNames.LeftForearmTwist2,
            BoneNames.LeftForearmTwist3,
            BoneNames.RightForearmTwist2,
            BoneNames.RightForearmTwist3,
            BoneNames.LeftPalm,
            BoneNames.RightPalm
        };

        //====================[ Motors ]====================
        // One drive-only motor per ragdoll joint, made while the bones are in the muscle pose (called from ZeroJointsAtMusclePose,
        // after the CharacterJoints have been set up again there). No drive yet: BuildMuscles sets it once the masses are final.
        private static void CreateMotors(Session session)
        {
            var parts = new Dictionary<Rigidbody, EBodyPartColliderType>();
            foreach (var body in session.Bodies) parts[body.Body] = body.Part;

            foreach (var state in session.Joints)
            {
                var parent = state.Joint.connectedBody;
                if (parent == null || session.Motors.ContainsKey(state.Body)) continue;
                bool hip = parts.TryGetValue(state.Body, out var part) && IsThigh(part);

                var motor = state.Body.gameObject.AddComponent<ConfigurableJoint>();
                motor.autoConfigureConnectedAnchor = false;
                motor.anchor = state.Joint.anchor;
                motor.connectedAnchor = state.Joint.connectedAnchor;
                // The hips drive flexion (the joint's x) apart from the rest (y, z); every other joint is one slerp drive.
                motor.axis = hip ? HipFlexionAxis : Vector3.right;
                motor.secondaryAxis = Vector3.up;
                motor.connectedBody = parent;
                motor.xMotion = ConfigurableJointMotion.Free;
                motor.yMotion = ConfigurableJointMotion.Free;
                motor.zMotion = ConfigurableJointMotion.Free;
                motor.angularXMotion = ConfigurableJointMotion.Free;
                motor.angularYMotion = ConfigurableJointMotion.Free;
                motor.angularZMotion = ConfigurableJointMotion.Free;
                motor.rotationDriveMode = hip ? RotationDriveMode.XYAndZ : RotationDriveMode.Slerp;
                motor.targetRotation = Quaternion.identity;
                motor.slerpDrive = default;
                motor.enableCollision = false;
                motor.enablePreprocessing = false;
                motor.massScale = 1f;
                motor.connectedMassScale = 1f;
                session.Motors[state.Body] = motor;
            }
        }

        // Switches every motor off and removes it. Off first: Destroy only takes effect at the end of the frame, and a body
        // handed on to EFT's corpse ragdoll simulates before then.
        private static void DestroyMotors(Session session)
        {
            foreach (var motor in session.Motors.Values)
            {
                if (motor == null) continue;
                motor.slerpDrive = default;
                motor.angularXDrive = default;
                motor.angularYZDrive = default;
                Object.Destroy(motor);
            }
            session.Motors.Clear();
        }

        //====================[ Build ]====================
        private static void BuildMuscles(Session session, SkeletonCopy pose)
        {
            var parentOf = new Dictionary<Rigidbody, Rigidbody>();
            foreach (var state in session.Joints)
            {
                if (state.Joint.connectedBody != null) parentOf[state.Body] = state.Joint.connectedBody;
            }

            var children = new Dictionary<Rigidbody, List<Rigidbody>>();
            foreach (var pair in parentOf)
            {
                if (!children.TryGetValue(pair.Value, out var list)) children[pair.Value] = list = new List<Rigidbody>();
                list.Add(pair.Key);
            }

            var joints = new Dictionary<Rigidbody, CharacterJoint>();
            foreach (var state in session.Joints) joints[state.Body] = state.Joint;

            var parts = new Dictionary<Rigidbody, EBodyPartColliderType>();
            foreach (var state in session.Bodies) parts[state.Body] = state.Part;

            foreach (var pair in parentOf)
            {
                var body = pair.Key;
                var parent = pair.Value;
                if (!session.Motors.TryGetValue(body, out var motor) || motor == null) continue;
                var part = parts.TryGetValue(body, out var p) ? p : EBodyPartColliderType.RibcageLow;

                var load = new List<Rigidbody>();
                CollectLoad(body, children, load);
                var joint = joints[body];

                session.Muscles.Add(new Muscle
                {
                    Body = body,
                    Parent = parent,
                    Joint = joint,
                    Drive = motor,
                    SettleRelative = Quaternion.Inverse(pose.Rotation(parent.transform)) * pose.Rotation(body.transform),
                    Group = ClassifyMuscle(part),
                    Part = part,
                    Inertia = JointInertia(body, parent, load, joint),
                    Weight = GravityMoment(body, load, joint)
                });
            }

            BuildNonPhysicalBones(session, pose);
            UpdateMuscleDrives(session);
        }

        private static void BuildNonPhysicalBones(Session session, SkeletonCopy pose)
        {
            session.NonPhysicalBones.Clear();
            var wanted = new HashSet<string>(PosedHelperBones);
            foreach (var bone in pose.AllBones)
            {
                if (bone == null || !wanted.Contains(bone.name)) continue;
                session.NonPhysicalBones.Add(new NonPhysicalBone { Bone = bone, Local = pose.LocalRotation(bone) });
            }
        }

        private static MuscleGroup ClassifyMuscle(EBodyPartColliderType part)
        {
            string name = part.ToString();
            if (name.IndexOf("Arm", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Forearm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return MuscleGroup.Arms;
            if (name.IndexOf("Thigh", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Calf", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Foot", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Toe", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Leg", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return MuscleGroup.Legs;
            if (name.IndexOf("Head", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Neck", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return MuscleGroup.NeckHead;
            return MuscleGroup.Spine;
        }

        private static void CollectLoad(Rigidbody body, Dictionary<Rigidbody, List<Rigidbody>> children, List<Rigidbody> load)
        {
            load.Add(body);
            if (!children.TryGetValue(body, out var list)) return;
            foreach (var child in list) CollectLoad(child, children, load);
        }

        //====================[ Drive ]====================
        // Puts the current settings on every motor; only rewrites a drive whose values changed, so the sliders apply live.
        private static void UpdateMuscleDrives(Session session)
        {
            const float dampingRatio = MuscleDampingRatio;
            foreach (var muscle in session.Muscles)
            {
                if (muscle.Drive == null) continue;
                float strength = Mathf.Max(0f, GroupStrength(muscle.Group));
                if (strength == muscle.AppliedStrength && dampingRatio == muscle.AppliedDamping) continue;

                muscle.AppliedStrength = strength;
                muscle.AppliedDamping = dampingRatio;
                float spring = strength * muscle.Inertia + PostureTone(muscle.Group, muscle.Part) * muscle.Weight;
                var drive = Drive(spring, muscle.Inertia, dampingRatio);
                if (IsThigh(muscle.Part))
                {
                    muscle.Drive.angularXDrive = Drive(spring * HipFlexionShare, muscle.Inertia, dampingRatio);
                    muscle.Drive.angularYZDrive = Drive(spring + HipSideTone * muscle.Weight, muscle.Inertia, dampingRatio);
                }
                else muscle.Drive.slerpDrive = drive;
            }
        }

        // Damped against the whole spring (critical at ratio 1 for the joint's inertia).
        private static JointDrive Drive(float spring, float inertia, float dampingRatio) => new JointDrive
        {
            positionSpring = spring,
            positionDamper = 2f * dampingRatio * Mathf.Sqrt(spring * inertia),
            maximumForce = float.MaxValue,
            useAcceleration = false
        };

        private static bool IsThigh(EBodyPartColliderType part) =>
            part == EBodyPartColliderType.LeftThigh || part == EBodyPartColliderType.RightThigh;

        // Muscle pull toward the pose (natural frequency squared, 1/s^2), and damping relative to critical.
        private const float ArmMuscleStrength = 120f;
        private const float LegMuscleStrength = 100f;
        private const float SpineMuscleStrength = 150f;
        private const float NeckHeadMuscleStrength = 110f;
        private const float MuscleDampingRatio = 1f;

        private static float GroupStrength(MuscleGroup group) => group switch
        {
            MuscleGroup.Arms => ArmMuscleStrength,
            MuscleGroup.Legs => LegMuscleStrength,
            MuscleGroup.NeckHead => NeckHeadMuscleStrength,
            _ => SpineMuscleStrength
        };

        private static float PostureTone(MuscleGroup group, EBodyPartColliderType part) => group switch
        {
            MuscleGroup.NeckHead => NeckPostureTone,
            MuscleGroup.Spine => SpinePostureTone,
            MuscleGroup.Arms => ArmPostureTone,
            _ => part == EBodyPartColliderType.LeftCalf || part == EBodyPartColliderType.RightCalf ? KneePostureTone : 0f
        };

        // The leaf bones without a body, held in the muscle pose (nothing else writes them while the physics runs).
        private static void PoseLeafBones(Session session)
        {
            foreach (var helper in session.NonPhysicalBones)
                if (helper.Bone != null) helper.Bone.localRotation = helper.Local;
        }

        //====================[ Helpers ]====================
        // How far (degrees, signed, about axis) a body is turned off a target turn relative to its parent.
        private static float AngleOff(Muscle muscle, Quaternion target, out Vector3 axis)
        {
            Quaternion error = muscle.Parent.rotation * target * Quaternion.Inverse(muscle.Body.rotation);
            return error.ToSignedAngleAxis(out axis);
        }

        // How hard the joint is to swing (kg m^2): everything it moves about the joint pivot against the parent on the other side
        // (the reduced inertia of the pair, since the motor turns both), averaged over the two axes across the bone. EFT's bones lie
        // along their local x, so that is the twist axis; a limb twists far more easily than it swings, but an implicit drive stays
        // stable on every axis, so the swing value sets the strength.
        private static float JointInertia(Rigidbody body, Rigidbody parent, List<Rigidbody> load, CharacterJoint joint)
        {
            Vector3 pivot = body.transform.TransformPoint(joint.anchor);
            Mat3 moved = Mat3.Zero;
            foreach (var part in load) moved = moved + InertiaTensorAbout(part, pivot);
            Mat3 other = InertiaTensorAbout(parent, pivot);

            Vector3 along = body.transform.right;
            Vector3 across1 = Vector3.Cross(along, Mathf.Abs(Vector3.Dot(along, Vector3.up)) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 across2 = Vector3.Cross(along, across1).normalized;

            float Reduced(Vector3 n)
            {
                float a = Vector3.Dot(n, moved * n), b = Vector3.Dot(n, other * n);
                return a + b > 1e-6f ? a * b / (a + b) : 0f;
            }
            return 0.5f * (Reduced(across1) + Reduced(across2));
        }

        // The most torque gravity can put on the joint (N*m): the weight of everything it moves at the distance of their common
        // centre of mass from the pivot (the lever held level).
        private static float GravityMoment(Rigidbody body, List<Rigidbody> load, CharacterJoint joint)
        {
            Vector3 pivot = body.transform.TransformPoint(joint.anchor);
            float mass = 0f;
            Vector3 weighted = Vector3.zero;
            foreach (var part in load)
            {
                mass += part.mass;
                weighted += part.mass * part.worldCenterOfMass;
            }
            if (mass <= 0f) return 0f;
            return mass * -Physics.gravity.y * Vector3.Distance(weighted / mass, pivot);
        }

        // How a body resists turning about a pivot, as a world-space 3x3 tensor: its own inertia plus the parallel-axis
        // term for its mass at its distance from the pivot.
        private static Mat3 InertiaTensorAbout(Rigidbody body, Vector3 pivot)
        {
            Quaternion frame = body.rotation * body.inertiaTensorRotation;
            Vector3 t = body.inertiaTensor;
            Vector3 x = frame * Vector3.right, y = frame * Vector3.up, z = frame * Vector3.forward;
            Mat3 own = Mat3.Outer(x, x) * t.x + Mat3.Outer(y, y) * t.y + Mat3.Outer(z, z) * t.z;
            Vector3 r = body.worldCenterOfMass - pivot;
            Mat3 offset = (Mat3.Identity * r.sqrMagnitude - Mat3.Outer(r, r)) * body.mass;
            return own + offset;
        }

        private readonly struct Mat3
        {
            private readonly float m00, m01, m02, m10, m11, m12, m20, m21, m22;

            private Mat3(float a, float b, float c, float d, float e, float f, float g, float h, float i)
            {
                m00 = a; m01 = b; m02 = c; m10 = d; m11 = e; m12 = f; m20 = g; m21 = h; m22 = i;
            }

            public static Mat3 Zero => new Mat3(0, 0, 0, 0, 0, 0, 0, 0, 0);
            public static Mat3 Identity => new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

            public static Mat3 Outer(Vector3 u, Vector3 v) => new Mat3(
                u.x * v.x, u.x * v.y, u.x * v.z,
                u.y * v.x, u.y * v.y, u.y * v.z,
                u.z * v.x, u.z * v.y, u.z * v.z);

            public static Mat3 operator +(Mat3 a, Mat3 b) => new Mat3(
                a.m00 + b.m00, a.m01 + b.m01, a.m02 + b.m02,
                a.m10 + b.m10, a.m11 + b.m11, a.m12 + b.m12,
                a.m20 + b.m20, a.m21 + b.m21, a.m22 + b.m22);

            public static Mat3 operator -(Mat3 a, Mat3 b) => new Mat3(
                a.m00 - b.m00, a.m01 - b.m01, a.m02 - b.m02,
                a.m10 - b.m10, a.m11 - b.m11, a.m12 - b.m12,
                a.m20 - b.m20, a.m21 - b.m21, a.m22 - b.m22);

            public static Mat3 operator *(Mat3 a, float s) => new Mat3(
                a.m00 * s, a.m01 * s, a.m02 * s, a.m10 * s, a.m11 * s, a.m12 * s, a.m20 * s, a.m21 * s, a.m22 * s);

            public static Vector3 operator *(Mat3 a, Vector3 v) => new Vector3(
                a.m00 * v.x + a.m01 * v.y + a.m02 * v.z,
                a.m10 * v.x + a.m11 * v.y + a.m12 * v.z,
                a.m20 * v.x + a.m21 * v.y + a.m22 * v.z);
        }
    }
}
