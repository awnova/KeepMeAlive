//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Rig ]====================
    // Putting the ragdoll bodies, joints and contact colliders on the player's skeleton.
    internal static partial class BodyRagdoll
    {
        //====================[ Contact Colliders ]====================
        // Adds a ground-contact copy without changing the hit collider, symmetrizing left/right limb capsules
        // (EFT's prefab has a thicker/longer capsule on RCalf than LCalf and different centers on LForearm/RForearm).
        private static Collider AddContact(Rigidbody body, EBodyPartColliderType part, PhysicMaterial material, Session session)
        {
            var host = new GameObject(ContactObjectName) { layer = LayersMaskController.ShellsLayer };
            host.transform.SetParent(body.transform, false);

            var copy = CopyShape(body.GetComponent<Collider>(), part, host);
            copy.sharedMaterial = material;
            session.Contacts.Add(copy);
            return copy;
        }

        // Every ragdoll bone of the player prefab carries exactly one capsule, box or sphere.
        private static Collider CopyShape(Collider source, EBodyPartColliderType part, GameObject host)
        {
            switch (source)
            {
                case CapsuleCollider c:
                    var capsule = host.AddComponent<CapsuleCollider>();
                    capsule.direction = c.direction;
                    switch (part)
                    {
                        case EBodyPartColliderType.LeftThigh:
                        case EBodyPartColliderType.RightThigh:
                            capsule.center = new Vector3(-0.228f, 0f, 0f);
                            capsule.radius = 0.095f;
                            capsule.height = 0.485f;
                            break;
                        case EBodyPartColliderType.LeftCalf:
                        case EBodyPartColliderType.RightCalf:
                            capsule.center = new Vector3(-0.240f, 0f, 0f);
                            capsule.radius = 0.088f;
                            capsule.height = 0.495f;
                            break;
                        case EBodyPartColliderType.LeftUpperArm:
                        case EBodyPartColliderType.RightUpperArm:
                            capsule.center = new Vector3(-0.148f, 0f, 0f);
                            capsule.radius = 0.074f;
                            capsule.height = 0.296f;
                            break;
                        case EBodyPartColliderType.LeftForearm:
                        case EBodyPartColliderType.RightForearm:
                            capsule.center = new Vector3(-0.165f, 0f, 0f);
                            capsule.radius = 0.045f;
                            capsule.height = 0.305f;
                            break;
                        default:
                            capsule.center = c.center;
                            capsule.radius = c.radius;
                            capsule.height = c.height;
                            break;
                    }
                    return capsule;
                case BoxCollider b:
                    var box = host.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                    return box;
                case SphereCollider s:
                    var sphere = host.AddComponent<SphereCollider>();
                    sphere.center = s.center;
                    sphere.radius = s.radius;
                    return sphere;
                default:
                    throw new InvalidOperationException($"unexpected ragdoll collider {source.GetType().Name} on {source.name}");
            }
        }

        //====================[ Limb Spacers ]====================
        // The parts of the body never collide with each other: the contact colliders are on the Shells layer, which does not
        // collide with itself. So the legs passed through each other and the arms into the chest. A spacer is a slimmer copy of a
        // limb's contact on the CullingMask layer, which collides with Shells and nothing else (not the world, not players, not
        // bullets): the left leg's spacers meet the right leg's contacts, and each arm's spacers meet the torso and the legs. A
        // spacer ignores its own body and the bodies jointed to it (they touch at the joint by design). Slimmer and kept off the
        // joint ends, so a body lying in the muscle pose starts clear and nothing pushes it at rest.
        private const string SpacerObjectName = "KMA_RagdollSpacer";

        private static readonly (EBodyPartColliderType Part, float Center, float Height, float Radius)[] SpacerShapes =
        {
            // Along each bone's -x (m): centre, length end to end, radius.
            (EBodyPartColliderType.LeftThigh, -0.26f, 0.40f, 0.07f),
            (EBodyPartColliderType.LeftCalf, -0.26f, 0.42f, 0.07f),
            (EBodyPartColliderType.LeftUpperArm, -0.17f, 0.25f, 0.06f),
            (EBodyPartColliderType.RightUpperArm, -0.17f, 0.25f, 0.06f),
            (EBodyPartColliderType.LeftForearm, -0.17f, 0.28f, 0.04f),
            (EBodyPartColliderType.RightForearm, -0.17f, 0.28f, 0.04f)
        };

        private static void AddLimbSpacers(Session session, PhysicMaterial material)
        {
            int layer = LayerMask.NameToLayer("CullingMask");
            if (layer < 0) return;

            var neighbours = new System.Collections.Generic.Dictionary<Rigidbody, System.Collections.Generic.HashSet<Rigidbody>>();
            void Link(Rigidbody a, Rigidbody b)
            {
                if (!neighbours.TryGetValue(a, out var set)) neighbours[a] = set = new System.Collections.Generic.HashSet<Rigidbody>();
                set.Add(b);
            }
            foreach (var state in session.Joints)
            {
                var parent = state.Joint.connectedBody;
                if (parent == null) continue;
                Link(state.Body, parent);
                Link(parent, state.Body);
            }

            foreach (var shape in SpacerShapes)
            {
                if (!session.BodyByPart.TryGetValue(shape.Part, out var body) || body == null) continue;

                var host = new GameObject(SpacerObjectName) { layer = layer };
                host.transform.SetParent(body.transform, false);
                var spacer = host.AddComponent<CapsuleCollider>();
                spacer.direction = 0;
                spacer.center = new Vector3(shape.Center, 0f, 0f);
                spacer.height = shape.Height;
                spacer.radius = shape.Radius;
                spacer.sharedMaterial = material;
                session.Spacers.Add(spacer);

                neighbours.TryGetValue(body, out var near);
                foreach (var contact in session.Contacts)
                {
                    var other = contact.attachedRigidbody;
                    if (other == body || (near != null && near.Contains(other))) Physics.IgnoreCollision(spacer, contact, true);
                }
            }
        }

        // Ground grip for the limbs, from GroundFriction (sliding friction decelerates at mu * g).
        // Uses Maximum friction combine so low-friction floor meshes do not halve the ragdoll's ground traction.
        private static PhysicMaterial ContactMaterial()
        {
            if (_contactMaterial == null)
            {
                const float mu = GroundFriction / 9.81f;
                _contactMaterial = new PhysicMaterial("KMA_RagdollContact")
                {
                    dynamicFriction = mu,
                    staticFriction = mu * 1.15f,
                    bounciness = 0f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Maximum
                };
            }
            return _contactMaterial;
        }

        //====================[ Human Segment Mass & Damping ]====================
        // EFT's corpse prefab assigns 5.625 kg to each forearm and 5.625 kg to each upper arm (making the two arms
        // 22.5 kg - heavier than both legs or the trunk!), which yanks the shoulders around and causes severe limb sag.
        // While the living ragdoll runs, I apply human body-segment masses (about 80 kg, pyramid-weighted
        // from trunk to extremities) and natural damping, restoring the prefab values in StopPhysics.
        private static float HumanSegmentMass(EBodyPartColliderType part)
        {
            switch (part)
            {
                case EBodyPartColliderType.Pelvis: return 14.0f;
                case EBodyPartColliderType.RibcageLow: return 12.0f;
                case EBodyPartColliderType.RibcageUp: return 14.0f;
                case EBodyPartColliderType.HeadCommon: return 4.5f;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm: return 2.4f;
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm: return 1.6f;
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh: return 9.0f;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf: return 4.5f;
                default: return 5.0f;
            }
        }

        //====================[ Anatomical Joint Limits ]====================
        // In Unity's PhysX CharacterJoint, twist is measured from child to parent: a child rotation of +theta about
        // joint.axis goes toward lowTwistLimit (-theta), and a child rotation of -theta about joint.axis goes toward
        // highTwistLimit (+theta).
        //   * On Spine2, Spine3, Head, Forearm (axis = (0, 0, -1)): anterior flexion (-Z child rotation = +theta about axis)
        //     is bounded by lowTwistLimit, and posterior extension (+Z child rotation) is bounded by highTwistLimit.
        //   * On Thigh (axis = (0, 0, +1)): anterior hip flexion (-Z child rotation = -theta about axis) is bounded by
        //     highTwistLimit (+75 deg), and hip extension (+Z child rotation) is bounded by lowTwistLimit (-20 deg).
        //   * On Calf (axis = (0, 0, +1)): knee flexion (+Z child rotation = +theta about axis) is bounded by
        //     lowTwistLimit (-95 deg), and knee hyperextension (-Z child rotation) is bounded by highTwistLimit (+2 deg).
        private static void ConfigureAnatomicalJoint(CharacterJoint joint, EBodyPartColliderType part)
        {
            var zeroSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
            joint.twistLimitSpring = zeroSpring;
            joint.swingLimitSpring = zeroSpring;

            switch (part)
            {
                case EBodyPartColliderType.RibcageLow: // Spine2 on Pelvis
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -35f, 15f, 32f, 32f);
                    break;

                case EBodyPartColliderType.RibcageUp: // Spine3 on Spine2
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -40f, 15f, 25f, 25f);
                    break;

                case EBodyPartColliderType.HeadCommon: // Head on Spine3
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -45f, 38f, 25f, 25f);
                    break;

                case EBodyPartColliderType.LeftUpperArm:
                    joint.axis = new Vector3(1f, 0f, 0f);
                    joint.swingAxis = new Vector3(0f, 0f, -1f);
                    SetLimits(joint, -45f, 45f, 45f, 45f);
                    break;

                case EBodyPartColliderType.RightUpperArm:
                    joint.axis = new Vector3(-1f, 0f, 0f);
                    joint.swingAxis = new Vector3(0f, 0f, -1f);
                    SetLimits(joint, -45f, 45f, 45f, 45f);
                    break;

                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    joint.axis = new Vector3(0f, 0f, -1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -125f, 20f, 55f, 55f);
                    break;

                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                    joint.axis = new Vector3(0f, 0f, 1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    SetLimits(joint, -25f, 80f, 48f, 48f);
                    break;

                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf:
                    joint.axis = new Vector3(0f, 0f, 1f);
                    joint.swingAxis = new Vector3(0f, 1f, 0f);
                    // A knee is a hinge: 5 deg of side play, not 25. With 25 the heel's ground drag bent the shin sideways at
                    // the knee until it sat on that limit (log 2026-10-19: calves 19-26 deg sideways while dragged), so a lower
                    // leg slowly splayed out instead of trailing.
                    SetLimits(joint, -115f, 10f, 5f, 5f);
                    break;
            }
        }

        private static void SetLimits(CharacterJoint joint, float lowTwist, float highTwist, float swing1, float swing2)
        {
            joint.lowTwistLimit = new SoftJointLimit { limit = lowTwist, bounciness = 0f, contactDistance = 0f };
            joint.highTwistLimit = new SoftJointLimit { limit = highTwist, bounciness = 0f, contactDistance = 0f };
            joint.swing1Limit = new SoftJointLimit { limit = swing1, bounciness = 0f, contactDistance = 0f };
            joint.swing2Limit = new SoftJointLimit { limit = swing2, bounciness = 0f, contactDistance = 0f };
        }

        // Explicitly disables PhysX internal collisions between all colliders belonging to this ragdoll so adjacent
        // socket colliders or external collision-matrix mods never cause self-depenetration chatter.
        private static void IgnoreInternalCollisions(Session session)
        {
            var allColliders = new System.Collections.Generic.List<Collider>(session.Contacts);
            foreach (var state in session.Bodies)
            {
                if (state.Body == null) continue;
                var hitCol = state.Body.GetComponent<Collider>();
                if (hitCol != null) allColliders.Add(hitCol);
            }

            int count = allColliders.Count;
            for (int i = 0; i < count; i++)
            {
                var a = allColliders[i];
                if (a == null) continue;
                for (int j = i + 1; j < count; j++)
                {
                    var b = allColliders[j];
                    if (b == null) continue;
                    Physics.IgnoreCollision(a, b, true);
                }
            }
        }

        //====================[ Seat On Joints ]====================
        // Moves each body onto its joint (parent first, so what hangs below follows), so the physics starts from a
        // pose its joints agree with.
        private static void SeatOnJoints(Session session)
        {
            bool moved = false;
            foreach (var state in session.Joints)
            {
                var joint = state.Joint;
                var parent = joint.connectedBody;
                if (parent == null) continue;
                Vector3 gap = parent.transform.TransformPoint(joint.connectedAnchor) - state.Body.transform.TransformPoint(joint.anchor);
                if (gap.sqrMagnitude < 1e-6f) continue;
                state.Body.transform.position += gap;
                moved = true;
            }
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        //====================[ Joint Zero ]====================
        private const float JointZeroStep = 0.001f;

        // Zeroes all CharacterJoints at the muscle pose and makes the muscle motors there, while every body is still kinematic,
        // then restores the current visual bone pose on screen.
        private static void ZeroJointsAtMusclePose(Player player, Session session, SkeletonCopy settlePose)
        {
            Transform pelvis = player.PlayerBones.Pelvis.Original;
            Transform[] all = pelvis.GetComponentsInChildren<Transform>(true);
            var savedRot = new Quaternion[all.Length];
            var savedPos = new Vector3[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                savedRot[i] = all[i].localRotation;
                savedPos[i] = all[i].localPosition;
            }

            try
            {
                foreach (var bone in all)
                {
                    if (bone != pelvis && CleanLocal.TryGetValue(bone, out var cleanPos))
                        bone.localPosition = cleanPos;
                    if (bone == pelvis || !settlePose.Has(bone) || !settlePose.Has(bone.parent)) continue;
                    bone.localRotation = Quaternion.Inverse(settlePose.Rotation(bone.parent)) * settlePose.Rotation(bone);
                }
                PhysicsExtensions.SyncTransforms();

                var partOf = new System.Collections.Generic.Dictionary<Rigidbody, EBodyPartColliderType>();
                foreach (var b in session.Bodies) partOf[b.Body] = b.Part;

                foreach (var state in session.Joints)
                {
                    var joint = state.Joint;
                    var parent = joint.connectedBody;
                    if (parent == null) continue;
                    if (partOf.TryGetValue(state.Body, out var part))
                        ConfigureAnatomicalJoint(joint, part);
                    Vector3 anchor = joint.connectedAnchor;
                    joint.connectedBody = null;
                    joint.connectedBody = parent;
                    joint.connectedAnchor = anchor;
                }

                // The muscle motors, made in the same pose, so their zero (and target) is the muscle pose.
                CreateMotors(session);

                PhysicsExtensions.SyncTransforms();
                bool ran = PhysicsExtensions.Simulation.CanRunSimulate;
                PhysicsExtensions.Simulation.Simulate(JointZeroStep);
                RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId}: joints zeroed at SettlePose (physics ran: {ran})");
            }
            finally
            {
                for (int i = 0; i < all.Length; i++)
                {
                    all[i].localPosition = savedPos[i];
                    all[i].localRotation = savedRot[i];
                }
                PhysicsExtensions.SyncTransforms();
            }
        }
    }
}
