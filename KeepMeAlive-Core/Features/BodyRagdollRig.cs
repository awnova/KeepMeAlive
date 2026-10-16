//====================[ Imports ]====================
using System;
using EFT;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Rig ]====================
    // Putting the ragdoll bodies, joints and contact colliders on the player's skeleton.
    internal static partial class BodyRagdoll
    {
        //====================[ Contact Colliders ]====================
        // Adds a ground-contact copy without changing the hit collider.
        private static Collider AddContact(Rigidbody body, PhysicMaterial material, Session session)
        {
            var host = new GameObject(ContactObjectName) { layer = LayersMaskController.ShellsLayer };
            host.transform.SetParent(body.transform, false);

            var copy = CopyShape(body.GetComponent<Collider>(), host);
            copy.sharedMaterial = material;
            session.Contacts.Add(copy);
            return copy;
        }

        // Every ragdoll bone of the player prefab carries exactly one capsule, box or sphere.
        private static Collider CopyShape(Collider source, GameObject host)
        {
            switch (source)
            {
                case CapsuleCollider c:
                    var capsule = host.AddComponent<CapsuleCollider>();
                    capsule.center = c.center;
                    capsule.radius = c.radius;
                    capsule.height = c.height;
                    capsule.direction = c.direction;
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

        // Ground grip for the limbs, from GroundFriction (sliding friction decelerates at mu * g).
        private static PhysicMaterial ContactMaterial()
        {
            if (_contactMaterial == null)
            {
                const float mu = GroundFriction / 9.81f;
                _contactMaterial = new PhysicMaterial("KMA_RagdollContact")
                {
                    dynamicFriction = mu,
                    staticFriction = mu,
                    bounciness = 0f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Average
                };
            }
            return _contactMaterial;
        }

        //====================[ Seat On Joints ]====================
        // Moves each body onto its joint (parent first, so what hangs below follows), so the physics starts from a
        // pose its joints agree with. The upper arms need it most: they hang from collarbones that have no body of
        // their own, so they start wherever the breathing loop's shrug left them, about 19 cm from the point on the
        // chest their joints hold them at. Left there, the first step would yank them across.
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
                // Through the transform, so the bones below follow in the same move.
                state.Body.transform.position += gap;
                moved = true;
            }
            // The game steps physics itself with transform auto-sync off.
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        //====================[ Joint Zero ]====================
        // The physics step the joints are built with (s): only long enough for it to run.
        private const float JointZeroStep = 0.001f;

        // Makes the muscle pose the joints' zero. A joint measures its limits from the turn between its two bodies when
        // it is set up, and EFT's are set up the first time the physics runs on a body - here, the breathing pose's,
        // with the left knee bent 109 degrees and the right arm across the chest. The muscles then pull past those
        // limits toward the muscle pose (the knee can never straighten) and the body buzzes against them. So, within
        // this frame and before the physics steps, the bones are turned to the muscle pose, every joint takes it as its
        // zero (setting the connected body again sets it up afresh), and the bones go straight back: the body is never
        // drawn in it. Where each joint is anchored is left as it was.
        private static void ZeroJointsAtMusclePose(Player player, Session session, SkeletonCopy musclePose)
        {
            Transform pelvis = player.PlayerBones.Pelvis.Original;
            Transform[] all = pelvis.GetComponentsInChildren<Transform>(true);
            var saved = new Quaternion[all.Length];
            for (int i = 0; i < all.Length; i++) saved[i] = all[i].localRotation;

            try
            {
                foreach (var bone in all)
                {
                    if (bone == pelvis || !musclePose.Has(bone) || !musclePose.Has(bone.parent)) continue;
                    bone.localRotation = Quaternion.Inverse(musclePose.Rotation(bone.parent)) * musclePose.Rotation(bone);
                }
                PhysicsExtensions.SyncTransforms();

                foreach (var state in session.Joints)
                {
                    var joint = state.Joint;
                    var parent = joint.connectedBody;
                    if (parent == null) continue;
                    Vector3 anchor = joint.connectedAnchor;
                    joint.connectedBody = null;
                    joint.connectedBody = parent;
                    joint.connectedAnchor = anchor;
                }

                // Unity works a joint's frames (its zero) out from the bodies' poses the next time the physics runs, not when the
                // joint is set. The game only steps physics in its own Update, by which time the bones below are back in the pose
                // they were in, and every joint would take that as its zero: its limits sit around the wrong pose and the muscles
                // fight them for good (the logs: the forearms 26 deg off with a swing limit of 0, the head and the thighs a steady
                // 38-41 deg off, the same as the breathing pose is from the muscle pose). So the physics runs for a moment here,
                // with the bones in the muscle pose and every body still kinematic (ActivateBodies comes after), and the joints are
                // built there.
                PhysicsExtensions.SyncTransforms();
                bool ran = PhysicsExtensions.Simulation.CanRunSimulate;
                PhysicsExtensions.Simulation.Simulate(JointZeroStep);
                RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId}: joints zeroed at the muscle pose (physics ran: {ran})");
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) all[i].localRotation = saved[i];
                PhysicsExtensions.SyncTransforms();
            }
        }
    }
}
