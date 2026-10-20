//====================[ Imports ]====================
using System;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace KeepMeAlive.Presentation.Spectating
{
    //====================[ LocalBodyCamera ]====================
    // The local player's first person view while their body is a physics ragdoll (BodyRagdoll, dragged or lying where it was
    // let go): from the head, turned as the head is, as Fika's head camera shows it to a spectator. The game's own view
    // (Cam, under Spine3, turned by the mouse look) keeps looking the way the mouse says while the body swings under it. Put
    // on the camera after every late pass and again just before it renders, as DownedSpectateCam does; a spectate of a
    // teammate takes the camera for itself.
    [DefaultExecutionOrder(BodyAnimation.LateWriteOrder)]
    internal sealed class LocalBodyCamera : MonoBehaviour
    {
        // The camera is kept to the capsule's side of the world (see DownedPose.FollowCamera).
        private const float BraceHeight = 0.5f;
        private const float WallClearance = 0.08f;

        private Player _player;
        private Transform _head;
        private bool _failed;

        public static LocalBodyCamera Attach(Player player)
        {
            var head = player.PlayerBones != null && player.PlayerBones.Head != null ? player.PlayerBones.Head.Original : null;
            if (head == null) return null;
            var view = player.gameObject.AddComponent<LocalBodyCamera>();
            view._player = player;
            view._head = head;
            return view;
        }

        // The camera on the head, turned as the head is, kept on the capsule's side of whatever is between them. Also used by
        // DownedPose while the body lies on its back (EFT's prone view, kept when it is really prone, would face the ground).
        public static void Place(Player player, Transform head, Camera main)
        {
            Vector3 position = head.TransformPoint(DownedSpectate.HeadOffset);
            Quaternion rotation = head.rotation * Quaternion.Euler(DownedSpectate.HeadEuler);
            if (Physics.Linecast(player.Position + Vector3.up * BraceHeight, position, out var hit, LayersMaskController.HighPolyWithTerrainMask))
                position = hit.point + hit.normal * WallClearance;
            main.transform.SetPositionAndRotation(position, rotation);
        }

        private void OnEnable() => Camera.onPreCull += Follow;
        private void OnDisable() => Camera.onPreCull -= Follow;
        private void LateUpdate() => Follow(null);

        private void Follow(Camera camera)
        {
            try
            {
                var player = _player;
                if (_failed || player == null || _head == null || !player.FirstPersonPointOfView || DownedSpectate.IsActive) return;
                if (!CameraManager.Exist) return;
                var main = CameraManager.Instance.Camera;
                if (main == null || (camera != null && camera != main)) return;

                Place(player, _head, main);
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.LogSource.LogWarning($"[LocalBodyCamera] Follow failed: {ex.Message}");
            }
        }
    }
}
