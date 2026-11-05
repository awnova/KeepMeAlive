//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: EFT Hand-Over ]====================
    // Taking the body away from EFT's animation and giving it back exactly as it was.
    internal static partial class BodyRagdoll
    {
        //====================[ EFT Hand-Over ]====================
        // Takes the body away from EFT's animation and IK. The same things Player.OnDead parks before handing the body
        // to the corpse ragdoll, except the character controller. Its collider's bounds are what the player's
        // TriggerColliderSearcher puts the player in audio rooms with; disabled, the bounds collapse to the world
        // origin, the player leaves every room, and SpatialAudioSystem treats their voice as coming from nowhere and
        // occludes it fully. What it changes is recorded on the session for ReattachToEft, its exact mirror.
        private static void DetachFromEft(Player player, Session session)
        {
            session.EnabledAnimators = player.EnabledAnimators;
            session.ComponentWasEnabled = player.enabled;
            session.BodyAnimatorWasEnabled = player.BodyAnimatorCommon.enabled;
            session.ArmsAnimatorWasEnabled = player.ArmsAnimatorCommon.enabled;

            // The local body is not taken from EFT: its component keeps running (the camera, the first person arms and
            // the movement states all hang off it), and Fika's own state handler stays. DownedPose writes the bones
            // after EFT's pass; only the physics suspends the body animation (SuspendLocalAnimation).
            if (player.IsYourPlayer) return;

            PlayerHooks.DetachStateChanged(player);
            PlayerHooks.DetachWeaponAnimation(player);

            player.EnabledAnimators = 0;
            player.BodyAnimatorCommon.enabled = false;
            player.ArmsAnimatorCommon.enabled = false;

            if (player.POM != null) player.POM.Off();

            // The whole IK/head pass runs from the component's LateUpdate; disabling it stops that.
            // Position updates come from Fika's network loop, so the player root still follows the owner.
            player.enabled = false;
        }

        // Gives the body back to EFT, as DetachFromEft found it.
        private static void ReattachToEft(Player player, Session session)
        {
            if (player.IsYourPlayer)
            {
                ResumeLocalAnimation(player, session);
                return;
            }

            player.BodyAnimatorCommon.enabled = session.BodyAnimatorWasEnabled;
            player.ArmsAnimatorCommon.enabled = session.ArmsAnimatorWasEnabled;
            player.EnabledAnimators = session.EnabledAnimators;

            if (player.POM != null) player.POM.On();

            player.enabled = session.ComponentWasEnabled;

            // Never twice, even when Exit undoes a hand-over that failed halfway.
            PlayerHooks.AttachWeaponAnimation(player);
            PlayerHooks.AttachStateChanged(player);
        }

        //====================[ Local Body ]====================
        // While the local body simulates, EFT's body animation and IK would rewrite the bones the physics drives; they
        // stop for that time. The first person arms (Arms, Procedural) and the component keep running.
        private const Player.EAnimatorMask LocalBodyMask = Player.EAnimatorMask.Thirdperson | Player.EAnimatorMask.FBBIK | Player.EAnimatorMask.IK;

        private static void SuspendLocalAnimation(Player player, Session session)
        {
            if (!player.IsYourPlayer || session.LocalSuspended) return;
            session.LocalSuspended = true;
            session.LocalMaskWas = player.EnabledAnimators;
            session.LocalBodyAnimatorWas = player.BodyAnimatorCommon.enabled;
            session.LocalKeepStateWas = player.BodyAnimatorCommon.keepAnimatorControllerStateOnDisable;
            player.EnabledAnimators &= ~LocalBodyMask;
            // As the game does whenever it switches this animator off (Player.cs, the stationary weapon set-up): without it
            // switching it back on starts the controller from its default state, replaying the move into prone, whose
            // root motion (Transit2ProneState) would then walk the local player's capsule.
            player.BodyAnimatorCommon.keepAnimatorControllerStateOnDisable = true;
            player.BodyAnimatorCommon.enabled = false;
            // The prone states move the player by the animator's last root motion every tick (PronePlayerState); a disabled
            // animator is not evaluated, so what it holds is not this frame's. The drag and the knockback move the player
            // themselves (ForcedMotion).
            session.LocalIgnoreDeltaWas = player.MovementContext.IgnoreDeltaMovement;
            player.MovementContext.IgnoreDeltaMovement = true;
        }

        private static void ResumeLocalAnimation(Player player, Session session)
        {
            if (!session.LocalSuspended) return;
            session.LocalSuspended = false;
            // Only the bits taken off: anything the game changed meanwhile stays.
            player.EnabledAnimators |= session.LocalMaskWas & LocalBodyMask;
            player.BodyAnimatorCommon.enabled = session.LocalBodyAnimatorWas;
            player.BodyAnimatorCommon.keepAnimatorControllerStateOnDisable = session.LocalKeepStateWas;
            player.MovementContext.IgnoreDeltaMovement = session.LocalIgnoreDeltaWas;
        }

        // Every bone's local pose as EFT's animation left it.
        private static void CaptureRest(Player player, Session session)
        {
            var pelvis = PlayerUtils.GetPelvis(player);
            foreach (var t in pelvis.parent.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith(BoneNames.Prefix, StringComparison.Ordinal)) continue;
                session.Rest.Add(new RestBone { Bone = t, LocalPosition = t.localPosition, LocalRotation = t.localRotation });
                // EFT's animation only turns the bones; where each sits on its parent is the rig's own. Kept from the
                // first time this player is taken over, before any physics has stretched them.
                if (!CleanLocal.ContainsKey(t)) CleanLocal[t] = t.localPosition;
            }
        }

        // Puts every bone below the top of the ragdoll back where the rig has it on its parent. The physics leaves
        // each joint's stretch in the bones' local positions, and the animation after it (DownedPose) only turns
        // bones, so without this every drag would start from the last one's stretch: the upper arms, pinned to the
        // chest while their collarbones (no body of their own) lay wherever the loop had them, came back off their
        // collarbones by up to the shrug, and further each time.
        private static void RestoreBoneOffsets(Session session)
        {
            var tops = new HashSet<Transform>();
            foreach (var top in session.Tops) tops.Add(top.Transform);
            foreach (var rest in session.Rest)
            {
                var bone = rest.Bone;
                if (bone == null || tops.Contains(bone) || !CleanLocal.TryGetValue(bone, out var local)) continue;
                bone.localPosition = local;
            }
        }
    }
}
