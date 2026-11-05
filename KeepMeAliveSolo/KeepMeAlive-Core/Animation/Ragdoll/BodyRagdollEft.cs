//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: EFT Hand-Over ]====================
    // Capturing the local skeleton's rest pose before DownedPose writes the bones.
    internal static partial class BodyRagdoll
    {
        // Every bone's local pose as EFT's animation left it.
        private static void CaptureRest(Player player, Session session)
        {
            var pelvis = PlayerUtils.GetPelvis(player);
            foreach (var t in pelvis.parent.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith(BoneNames.Prefix, StringComparison.Ordinal)) continue;
                session.Rest.Add(new RestBone { Bone = t, LocalPosition = t.localPosition, LocalRotation = t.localRotation });
            }
        }
    }
}
