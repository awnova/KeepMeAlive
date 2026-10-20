//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Collision ]====================
    // Limbs snagged on the world. The parts of the body do not collide with each other (IgnoreInternalCollisions): the
    // muscles pull every joint toward the muscle pose, which holds the limbs apart.
    internal static partial class BodyRagdoll
    {
        //====================[ Snags ]====================
        // Repositions stretched limbs and briefly releases their contact colliders.
        private static void FixSnags(Session session)
        {
            bool moved = false;

            foreach (var state in session.Joints)
            {
                var joint = state.Joint;
                var limb = state.Body;
                var parent = joint.connectedBody;

                Vector3 limbAnchor = limb.transform.TransformPoint(joint.anchor);
                Vector3 parentAnchor = parent.transform.TransformPoint(joint.connectedAnchor);
                Vector3 gap = parentAnchor - limbAnchor;
                if (gap.sqrMagnitude < MaxJointStretch * MaxJointStretch) continue;

                // Through the transform, so the bones below follow in the same move.
                limb.transform.position += gap;
                limb.velocity = parent.velocity;
                limb.angularVelocity = parent.angularVelocity;
                ReleaseFromWorld(session, limb.transform);
                moved = true;
            }

            // The game drives physics itself with transform auto-sync off; without this the next step would
            // still see the stretched pose and write it back.
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        // Moves the contact colliders of a limb and everything below it onto a layer that collides with nothing.
        // The collider stays enabled (its bounds and shape stay valid for the overlap test that brings it back).
        private static void ReleaseFromWorld(Session session, Transform limb)
        {
            float until = Time.time + SnagReleaseMin;
            foreach (var collider in session.Contacts)
            {
                if (!collider.transform.IsChildOf(limb)) continue;

                collider.gameObject.layer = ReleasedLayer;
                // A limb that snags again restarts its minimum but keeps its first release time, so the hard
                // maximum still counts from the original snag.
                float since = session.Released.TryGetValue(collider, out var existing) ? existing.Since : Time.time;
                session.Released[collider] = new Release { Since = since, Until = until };
            }
        }

        // Brings released contacts back once they are no longer inside anything they would collide with.
        private static void UpdateReleased(Session session)
        {
            if (session.Released.Count == 0) return;

            _restored.Clear();
            foreach (var pair in session.Released)
            {
                var collider = pair.Key;
                if (Time.time < pair.Value.Until) continue;

                bool overdue = Time.time >= pair.Value.Since + SnagReleaseMax;
                if (overdue || !IsInsideWorld(collider))
                {
                    collider.gameObject.layer = LayersMaskController.ShellsLayer;
                    _restored.Add(collider);
                }
            }
            foreach (var collider in _restored) session.Released.Remove(collider);
        }

        private static bool IsInsideWorld(Collider collider)
        {
            Bounds bounds = collider.bounds;
            // The world only: Shells also collides with CullingMask, the layer of the body's own limb spacers.
            int spacers = LayerMask.NameToLayer("CullingMask");
            int mask = LayersMaskController.ShellsCollisionsMask & ~(spacers >= 0 ? 1 << spacers : 0);
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, _overlaps, Quaternion.identity,
                mask, QueryTriggerInteraction.Ignore);

            Transform t = collider.transform;
            for (int i = 0; i < count; i++)
            {
                var other = _overlaps[i];
                if (Physics.ComputePenetration(collider, t.position, t.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth)
                    && depth > SnagOverlapTolerance)
                {
                    return true;
                }
            }
            return false;
        }

        // Gives up on the ragdoll until it is no longer wanted; the body carries on as the plain (animated)
        // version.
        private static void AbortRagdoll(Session session, string reason, ExitPose pose)
        {
            MarkFailed(session.Player.ProfileId);
            Plugin.LogSource.LogWarning($"[BodyRagdoll] Aborting for {session.Player.ProfileId}: {reason}");
            Exit(session, pose);
        }
    }
}
