//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ HandFrames ]====================
    // Reads a hand's orientation off a skeleton from joint positions only (never from bone axes),
    // so it means the same on any skeleton.
    //
    //   FromKnuckles - the hand as a rigid object: for placing things in the hand (the stim pen) and fingers
    //                  relative to it (the stim's fingers).
    internal static class HandFrames
    {
        // The hand's frame from its joints: forward along palm -> middle knuckle, side along the index -> little knuckle
        // line, up = forward x side (Unity: Quaternion.LookRotation(forward, up)). The bakes build it the same way.
        public static Quaternion FromKnuckles(Vector3 palm, Vector3 middle, Vector3 index, Vector3 little)
        {
            Vector3 forward = (middle - palm).normalized;
            Vector3 across = index - little;
            across = (across - forward * Vector3.Dot(across, forward)).normalized;
            return Quaternion.LookRotation(forward, Vector3.Cross(forward, across));
        }

        // The hand's knuckle frame (FromKnuckles) relative to its palm bone; false without the knuckles.
        public static bool TryReadKnuckleFrame(Transform palm, bool left, out Quaternion frameLocal)
        {
            frameLocal = Quaternion.identity;
            if (!BoneNames.TryFindKnuckles(palm, left, out var middle, out var index, out var little)) return false;
            frameLocal = FromKnuckles(Vector3.zero, LocalOffset(palm, middle), LocalOffset(palm, index), LocalOffset(palm, little));
            return true;
        }

        // The offset of `bone` from `root` in `root`'s own space, walked from each bone's localPosition and localRotation
        // (unaffected by any scale or pose above `root`).
        public static Vector3 LocalOffset(Transform root, Transform bone)
        {
            if (root == null || bone == null) return Vector3.zero;
            if (!bone.IsChildOf(root)) return Quaternion.Inverse(root.rotation) * (bone.position - root.position);
            Vector3 offset = Vector3.zero;
            for (Transform t = bone; t != null && t != root; t = t.parent)
                offset = t.localPosition + t.localRotation * offset;
            return offset;
        }
    }
}
