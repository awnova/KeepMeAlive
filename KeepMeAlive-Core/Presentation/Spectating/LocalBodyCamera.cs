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

        // The game's view hangs under Spine3, this one is on the head: a switch between the two (the body taken over or
        // handed back) is eased over this long instead of cut.
        private const float HandoverSeconds = 1f;
        // Further apart than this the two views are not of the same place (a spectate ending): cut.
        private const float HandoverMaxMetres = 1f;

        private Player _player;
        private Transform _head;
        private bool _failed;

        // The head view as last written, and the frame it was written in.
        private static int _placedFrame = -1;
        private static Player _placedPlayer;
        private static Vector3 _placedPosition;
        private static Quaternion _placedRotation;
        // The switch being eased: how far the view being left was from the one taken over, at the switch.
        private static float _handoverStart = -1f;
        private static Vector3 _handoverOffset;
        private static Quaternion _handoverTurn;
        private static bool _handingBack;
        private static int _handBackFrame = -1;
        private static bool _hooked;

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
        public static void Place(Player player, Transform head, Camera main) =>
            Place(player, head, main, 0f, default, default);

        // toGame: how far (0 to 1) the view has slid from the head to the game's own (gamePosition, gameRotation), as
        // DownedPose does over the end of the roll.
        public static void Place(Player player, Transform head, Camera main, float toGame, Vector3 gamePosition, Quaternion gameRotation)
        {
            Vector3 position = head.TransformPoint(DownedSpectate.HeadOffset);
            Quaternion rotation = head.rotation * Quaternion.Euler(DownedSpectate.HeadEuler);
            if (Physics.Linecast(player.Position + Vector3.up * BraceHeight, position, out var hit, LayersMaskController.HighPolyWithTerrainMask))
                position = hit.point + hit.normal * WallClearance;
            if (toGame > 0f)
            {
                position = Vector3.Lerp(position, gamePosition, toGame);
                rotation = Quaternion.Slerp(rotation, gameRotation, toGame);
            }

            // Not written last frame: the camera still holds the game's view, which this eases out of.
            int frame = Time.frameCount;
            if (_placedFrame < frame - 1)
            {
                Vector3 from = main.transform.position;
                Quaternion fromRotation = main.transform.rotation;
                // A hand-back still easing (the head view gone for a frame or two): from what it has on screen.
                if (_handingBack) Ease(ref from, ref fromRotation);
                BeginHandover(from, fromRotation, position, rotation, false);
            }
            Ease(ref position, ref rotation);

            main.transform.SetPositionAndRotation(position, rotation);
            _placedFrame = frame;
            _placedPlayer = player;
            _placedPosition = position;
            _placedRotation = rotation;
            if (!_hooked)
            {
                Camera.onPreCull += HandBack;
                _hooked = true;
            }
        }

        // The first frame the head view is gone, the game's view eases in from where it left the camera. Only into the
        // game's own first person view: a spectate (KMA's or Fika's) takes the camera as it is.
        private static void HandBack(Camera camera)
        {
            try
            {
                if (!CameraManager.Exist) return;
                var main = CameraManager.Instance.Camera;
                int frame = Time.frameCount;
                if (main == null || camera != main || _placedFrame >= frame || _handBackFrame == frame) return;
                _handBackFrame = frame;

                if (!_handingBack && _placedFrame != frame - 1) return;
                var player = _placedPlayer;
                if (player == null || !player.FirstPersonPointOfView || DownedSpectate.IsActive || SpectateFika.FikaCamActive())
                {
                    _handoverStart = -1f;
                    _handingBack = false;
                    return;
                }
                if (_placedFrame == frame - 1) BeginHandover(_placedPosition, _placedRotation, main.transform.position, main.transform.rotation, true);
                if (!_handingBack || _handoverStart < 0f) return;

                // The game writes its view again every frame, so this eases that frame's view, never its own.
                Vector3 position = main.transform.position;
                Quaternion rotation = main.transform.rotation;
                Ease(ref position, ref rotation);
                main.transform.SetPositionAndRotation(position, rotation);
            }
            catch (Exception ex)
            {
                _handoverStart = -1f;
                Plugin.LogSource.LogWarning($"[LocalBodyCamera] Hand back failed: {ex.Message}");
            }
        }

        private static void BeginHandover(Vector3 fromPosition, Quaternion fromRotation, Vector3 toPosition, Quaternion toRotation, bool back)
        {
            _handoverStart = -1f;
            _handingBack = false;
            if ((fromPosition - toPosition).sqrMagnitude > HandoverMaxMetres * HandoverMaxMetres) return;
            _handoverOffset = fromPosition - toPosition;
            _handoverTurn = fromRotation * Quaternion.Inverse(toRotation);
            _handoverStart = Time.time;
            _handingBack = back;
        }

        // The view taken over, moved back toward the one left by what is left of the switch.
        private static void Ease(ref Vector3 position, ref Quaternion rotation)
        {
            if (_handoverStart < 0f) return;
            float left = 1f - Mathf.SmoothStep(0f, 1f, (Time.time - _handoverStart) / HandoverSeconds);
            if (left <= 0f)
            {
                _handoverStart = -1f;
                _handingBack = false;
                return;
            }
            position += _handoverOffset * left;
            rotation = Quaternion.Slerp(Quaternion.identity, _handoverTurn, left) * rotation;
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
