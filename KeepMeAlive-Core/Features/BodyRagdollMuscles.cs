//====================[ Imports ]====================
using System.Collections.Generic;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Muscles ]====================
    // The muscles that pull the body toward the muscle pose (the settle clip's first frame): springs that contract the
    // ragdoll toward it, against gravity and the floor. The same whether the body is dragged or let go.
    internal static partial class BodyRagdoll
    {
        //====================[ Muscles ]====================
        // A muscle on every joint, pulling toward the muscle pose (MusclePose): each body at the turn that pose has it
        // at relative to the one it hangs from. The body itself is never put into that pose; it is only where the
        // muscles pull to.
        private static void BuildMuscles(Session session, SkeletonCopy musclePose)
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
                var load = new List<Rigidbody>();
                CollectLoad(pair.Key, children, load);
                session.Muscles.Add(new Muscle
                {
                    Body = pair.Key,
                    Parent = pair.Value,
                    TargetRelative = Quaternion.Inverse(musclePose.Rotation(pair.Value.transform)) * musclePose.Rotation(pair.Key.transform),
                    Joint = joints[pair.Key],
                    Load = load.ToArray(),
                    Arm = parts.TryGetValue(pair.Key, out var armPart) && IsArm(armPart)
                });
            }
        }

        private static bool IsArm(EBodyPartColliderType part) =>
            part == EBodyPartColliderType.LeftUpperArm || part == EBodyPartColliderType.RightUpperArm
            || part == EBodyPartColliderType.LeftForearm || part == EBodyPartColliderType.RightForearm;

        // A body and everything hanging off it.
        private static void CollectLoad(Rigidbody body, Dictionary<Rigidbody, List<Rigidbody>> children, List<Rigidbody> load)
        {
            load.Add(body);
            if (!children.TryGetValue(body, out var list)) return;
            foreach (var child in list) CollectLoad(child, children, load);
        }

        //====================[ Apply ]====================
        // Turns each body toward its target relative to its parent, with an equal and opposite twist on the parent so
        // the body cannot push itself around. The Muscle Strength setting (see HoldDampingRatio), each muscle against
        // everything its joint moves, the torque shared with the body on the other side of the joint (the reduced
        // inertia of the pair: with a light parent, like an upper arm under a forearm, the reaction otherwise doubles
        // the gain and the pair shakes itself apart).
        private static void ApplyMuscles(Session session, float step)
        {
            float strength = KeepMeAliveSettings.MUSCLE_STRENGTH.Value;
            float stable = StableStepProduct / (step * step);
            foreach (var muscle in session.Muscles)
            {
                var body = muscle.Body;
                var parent = muscle.Parent;

                float stiffness = Mathf.Min(strength, stable);
                float damping = 2f * Mathf.Sqrt(stiffness) * HoldDampingRatio;
                float angle = AngleOff(muscle, muscle.TargetRelative, out Vector3 axis);
                Vector3 offset = Mathf.Abs(angle) > 0.01f && axis.IsFinite() ? axis * (angle * Mathf.Deg2Rad) : Vector3.zero;
                Vector3 spin = body.angularVelocity - parent.angularVelocity;

                // Stable PD (Tan, Liu & Turk 2011): the spring acts on where the body will be after the step and the
                // damping is solved for the step, so a slow frame (a longer step) settles instead of overshooting
                // and shaking.
                Vector3 acceleration = (stiffness * offset - (damping + stiffness * step) * spin)
                                       / (1f + damping * step + stiffness * step * step);
                Vector3 torque = ToTorqueAboutJoint(muscle, acceleration);
                // An arm hangs in the air in the muscle pose (the forearm is raised), so its muscle also carries its weight; the
                // spring alone would let it sag by gravity over stiffness, 20 to 30 degrees.
                if (muscle.Arm) torque += HoldWeight(muscle);
                body.AddTorque(torque);
                parent.AddTorque(-torque);
            }
        }

        // The torque that cancels the pull of gravity on everything the joint moves, about the joint.
        private static Vector3 HoldWeight(Muscle muscle)
        {
            Vector3 pivot = muscle.Joint.transform.TransformPoint(muscle.Joint.anchor);
            Vector3 weight = Vector3.zero;
            foreach (var body in muscle.Load)
                weight += Vector3.Cross(body.worldCenterOfMass - pivot, body.mass * Physics.gravity);
            return -weight;
        }

        //====================[ Helpers ]====================
        // How far (degrees, signed, about axis) a body is turned off a target turn relative to its parent.
        private static float AngleOff(Muscle muscle, Quaternion target, out Vector3 axis)
        {
            Quaternion error = muscle.Parent.rotation * target * Quaternion.Inverse(muscle.Body.rotation);
            return error.ToSignedAngleAxis(out axis);
        }

        // The torque that gives everything the joint moves this angular acceleration about the joint. The body on the
        // other side of the joint is turned the opposite way by the same torque, so the torque is scaled for the pair.
        private static Vector3 ToTorqueAboutJoint(Muscle muscle, Vector3 acceleration)
        {
            float magnitude = acceleration.magnitude;
            if (magnitude < 1e-6f) return Vector3.zero;
            Vector3 axis = acceleration / magnitude;
            Vector3 pivot = muscle.Joint.transform.TransformPoint(muscle.Joint.anchor);
            float inertia = 0f;
            foreach (var body in muscle.Load) inertia += InertiaAbout(body, pivot, axis);
            float parent = InertiaAbout(muscle.Parent, pivot, axis);
            inertia = inertia * parent / (inertia + parent);
            return acceleration * inertia;
        }

        // How hard a body is to turn about an axis through a pivot: its own inertia about that axis plus its mass at
        // its distance from it.
        private static float InertiaAbout(Rigidbody body, Vector3 pivot, Vector3 axis)
        {
            Vector3 local = Quaternion.Inverse(body.rotation * body.inertiaTensorRotation) * axis;
            Vector3 tensor = body.inertiaTensor;
            Vector3 arm = Vector3.ProjectOnPlane(body.worldCenterOfMass - pivot, axis);
            return local.x * local.x * tensor.x + local.y * local.y * tensor.y + local.z * local.z * tensor.z
                   + body.mass * arm.sqrMagnitude;
        }
    }
}
