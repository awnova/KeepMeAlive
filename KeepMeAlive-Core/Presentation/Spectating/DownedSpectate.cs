//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using EFT.InputSystem;
using EFT.UI;
using UnityEngine;

namespace KeepMeAlive.Presentation.Spectating
{
    //====================[ DownedSpectate ]====================
    // Lets a downed local player watch their living teammates, started and steered from the downed control strip.
    // The game stops writing the camera by the same route Fika's own spectate takes (the free-camera point of view);
    // DownedSpectateCam then sets the pose. Nothing of Fika's camera is called, patched or subscribed to: when Fika's
    // own camera takes over (extract, death) this gives way. See SpectateFika for every Fika member read.
    internal static class DownedSpectate
    {
        //====================[ Tuning ]====================
        // The same V starts and ends it (the HUD shows both); switching target is Fika's own spectate keys.
        internal const KeyCode EnterKey = KeyCode.V;
        internal const KeyCode ReturnKey = KeyCode.V;
        private const KeyCode NextKey = KeyCode.Mouse0;
        private const KeyCode PreviousKey = KeyCode.Mouse1;
        private const KeyCode ViewKey = KeyCode.Space;
        private const KeyCode VisionKey = KeyCode.N;

        // How long the camera holds its last pose with nobody left to follow before giving the view back.
        private const float NoTargetGraceSeconds = 1.5f;

        // Fika's third person and head cam placements, relative to the anchor (see SpectateFika.Anchor).
        private static readonly Vector3 ThirdOffset = new Vector3(0.3f, 0.2f, -0.65f);
        private static readonly Vector3 ThirdEuler = new Vector3(4.3f, 5.9f, 0f);
        internal static readonly Vector3 HeadOffset = new Vector3(-0.1f, -0.07f, -0.17f);
        internal static readonly Vector3 HeadEuler = new Vector3(260f, 80f, 0f);

        private enum ExitKind { Normal, Death, Yield }

        //====================[ State ]====================
        private static readonly List<Player> Targets = new List<Player>();
        private static Player _player;
        private static DownedSpectateCam _cam;
        private static Player _target;
        private static int _index;
        private static bool _headcam;
        private static int _enteredFrame = -1;
        // The key that returns to the body is also the one that starts spectating: the frame it ends must not restart it.
        private static int _exitedFrame = -1;
        private static float _noTargetSince = -1f;
        private static bool _fikaOwns;

        // Vision as it was on entry, and as this class has set it since.
        private static bool _nvWas, _thWas, _nvNow, _thNow;

        public static bool IsActive => _cam != null;

        // Commands the spectate keys would also trigger in the game while they are in use.
        public static bool BlocksCommand(ECommand command) =>
            command == ECommand.ToggleGoggles
            || command == ECommand.ToggleShooting || command == ECommand.EndShooting
            || command == ECommand.ToggleAlternativeShooting || command == ECommand.EndAlternativeShooting;

        //====================[ Per tick (local player) ]====================
        public static void Tick(Player player, RMPlayer st)
        {
            if (player == null || !player.IsYourPlayer) return;

            if (IsActive)
            {
                if (SpectateFika.FikaCamActive()) { Exit(ExitKind.Yield, "Fika camera took over"); return; }
                if (!player.HealthController.IsAlive) { Exit(ExitKind.Death, "dead"); return; }
                if (!CanContinue(player, st)) { Exit(ExitKind.Normal, "state changed"); return; }
                if (DownedRagdollController.DeliberateMove(player, st)) Exit(ExitKind.Normal, "moved");
                return;
            }

            bool available = CanStart(player, st);
            Hud.SetSpectate(available ? Hud.SpectateHint.Available : Hud.SpectateHint.Hidden);
            if (available && Time.frameCount != _exitedFrame && UnityEngine.Input.GetKeyDown(EnterKey)) Enter(player);
        }

        // The states in which the player's own view must stay theirs: a revive or a self-revive hold. Being dragged
        // does not count: a teammate can drag the body while its owner keeps watching.
        private static bool CanContinue(Player player, RMPlayer st) =>
            !DownedStateController.IsRaidEnding
            && st.State == RMState.BleedingOut
            && !st.IsBeingRevived && !st.IsSelfReviving && !st.SelfReviveAwaitingAuth;

        private static bool CanStart(Player player, RMPlayer st)
        {
            if (!CanContinue(player, st)) return false;
            if (!CameraManager.Exist || CameraManager.Instance.Camera == null) return false;
            if (SpectateFika.FikaCamActive()) return false;
            SpectateFika.GetTargets(Targets);
            return Targets.Count > 0;
        }

        //====================[ Enter / exit ]====================
        private static void Enter(Player player)
        {
            try
            {
                var camera = CameraManager.Instance.Camera;
                var body = player.PlayerBody;
                var controller = player.GetComponent<PlayerCameraController>();
                if (body == null || controller == null || Targets.Count == 0) return;

                _player = player;
                _headcam = false;
                _index = 0;
                _fikaOwns = false;
                _noTargetSince = -1f;
                _enteredFrame = Time.frameCount;
                SaveVision();

                // Fika's own recipe: the game's first person camera state stops writing the camera for the free camera view.
                player.PointOfView = EPointOfView.ThirdPerson;
                body.PointOfView.Value = EPointOfView.FreeCamera;
                controller.UpdatePointOfView();

                SetTarget(Targets[0]);
                _cam = camera.gameObject.AddComponent<DownedSpectateCam>();
                RevivalDebugLog.LogDebug($"[Spectate] enter, {Targets.Count} target(s), first={SpectateFika.Name(_target)}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Spectate] Enter failed: {ex}");
                Exit(ExitKind.Normal, "enter failed");
            }
        }

        // The one way out. Normal puts everything back; Death puts the first person view back so the death view and
        // Fika's own spectate start from the usual state; Yield leaves the view, point of view and vision to Fika.
        private static void Exit(ExitKind kind, string reason)
        {
            var player = _player;
            var cam = _cam;
            bool wasActive = cam != null;

            _cam = null;
            _player = null;
            _target = null;
            _noTargetSince = -1f;
            _exitedFrame = Time.frameCount;
            Hud.SetSpectate(Hud.SpectateHint.Hidden);
            if (cam != null) UnityEngine.Object.Destroy(cam);
            if (!wasActive) return;

            RevivalDebugLog.LogDebug($"[Spectate] exit ({kind}): {reason}");
            if (kind == ExitKind.Yield) return;

            try { RestoreVision(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[Spectate] vision restore failed: {ex.Message}"); }

            try
            {
                // The setter re-enters the game's first person camera state through PointOfViewChanged.
                if (player != null) player.PointOfView = EPointOfView.FirstPerson;
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[Spectate] point of view restore failed: {ex.Message}"); }
        }

        // Raid boundary: the camera and the player go with the scene, so nothing is touched, only forgotten.
        public static void Clear()
        {
            var cam = _cam;
            _cam = null;
            _player = null;
            _target = null;
            _noTargetSince = -1f;
            Hud.SetSpectate(Hud.SpectateHint.Hidden);
            if (cam != null) UnityEngine.Object.Destroy(cam);
        }

        // Death handoff (DownedStateController.PrepareForDeath).
        public static void OnDeath() { if (IsActive) Exit(ExitKind.Death, "death"); }

        //====================[ Per frame (camera component) ]====================
        public static void Frame()
        {
            if (!IsActive) return;

            _fikaOwns = SpectateFika.FikaCamActive();
            if (_fikaOwns) return; // Tick gives way this tick

            var player = _player;
            if (player == null || player.HealthController == null || !player.HealthController.IsAlive)
            {
                Exit(ExitKind.Death, "player gone or dead");
                return;
            }

            SpectateFika.GetTargets(Targets);
            if (Targets.Count == 0)
            {
                _target = null;
                if (_noTargetSince < 0f) _noTargetSince = Time.time;
                else if (Time.time - _noTargetSince >= NoTargetGraceSeconds)
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.Spectate.NoTeammates);
                    Exit(ExitKind.Normal, "no teammates");
                }
                return;
            }

            _noTargetSince = -1f;
            int at = Targets.IndexOf(_target);
            if (at >= 0) { _index = at; return; }

            // The one followed is gone: the next in line takes their place.
            _index = Mathf.Clamp(_index, 0, Targets.Count - 1);
            SetTarget(Targets[_index]);
        }

        // Where the camera goes this frame; false leaves it where it is.
        public static bool TryGetPose(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (_fikaOwns || _target == null) return false;

            Transform anchor = SpectateFika.Anchor(_target, _headcam);
            if (anchor == null) return false;

            Vector3 offset = _headcam ? HeadOffset : ThirdOffset;
            if (SpectateFika.IsLeftStance(_target))
            {
                if (_headcam) offset.z = -offset.z;
                else offset.x = -offset.x;
            }

            position = anchor.TransformPoint(offset);
            rotation = anchor.rotation * Quaternion.Euler(_headcam ? HeadEuler : ThirdEuler);
            return true;
        }

        //====================[ Input (camera component) ]====================
        public static void HandleInput()
        {
            if (!IsActive || Time.frameCount == _enteredFrame || ConsoleVisible()) return;

            if (UnityEngine.Input.GetKeyDown(ReturnKey)) Exit(ExitKind.Normal, "returned to body");
            else if (UnityEngine.Input.GetKeyDown(NextKey)) Cycle(1);
            else if (UnityEngine.Input.GetKeyDown(PreviousKey)) Cycle(-1);
            else if (UnityEngine.Input.GetKeyDown(ViewKey))
            {
                _headcam = !_headcam;
                ShowActive();
            }
            else if (UnityEngine.Input.GetKeyDown(VisionKey)) ToggleVision();
        }

        private static bool ConsoleVisible()
        {
            var ui = MonoBehaviourSingleton<PreloaderUI>.Instantiated ? MonoBehaviourSingleton<PreloaderUI>.Instance : null;
            return ui != null && ui.Console != null && ui.Console.IsConsoleVisible;
        }

        private static void Cycle(int step)
        {
            SpectateFika.GetTargets(Targets);
            if (Targets.Count == 0) return;
            int at = Targets.IndexOf(_target);
            if (at < 0) at = Mathf.Clamp(_index, 0, Targets.Count - 1);
            _index = (at + step + Targets.Count) % Targets.Count;
            SetTarget(Targets[_index]);
        }

        private static void SetTarget(Player target)
        {
            _target = target;
            RevivalDebugLog.LogDebug($"[Spectate] following {SpectateFika.Name(target)}");
            ShowActive();
        }

        private static void ShowActive() => Hud.SetSpectate(Hud.SpectateHint.Active, SpectateFika.Name(_target), _headcam, _nvNow);

        //====================[ Vision ]====================
        // The game's own switch (StartSwitch), not the On flag: the flag alone leaves the effect's components as they were.
        private static void SaveVision()
        {
            var cm = CameraManager.Instance;
            _nvWas = _nvNow = cm.NightVision != null && cm.NightVision.On;
            _thWas = _thNow = cm.ThermalVision != null && cm.ThermalVision.On;
        }

        // N is a plain night vision toggle; thermal (if it was on at entry) is switched off when night vision goes on.
        private static void ToggleVision()
        {
            SetVision(!_nvNow, false);
            ShowActive();
        }

        private static void RestoreVision() => SetVision(_nvWas, _thWas);

        private static void SetVision(bool nightVision, bool thermal)
        {
            var cm = CameraManager.Instance;
            if (cm == null) return;

            var nv = cm.NightVision;
            var th = cm.ThermalVision;
            bool nvUsable = nv != null && nv.enabled;
            bool thUsable = th != null && th.enabled;

            // Off first, so the two never overlap (the game fast-forwards the other one the same way).
            if (nvUsable && _nvNow && !nightVision) { nv.StartSwitch(false); nv.FastForwardSwitch(); }
            if (thUsable && _thNow && !thermal) { th.StartSwitch(false); th.FastForwardSwitch(); }
            if (nvUsable && !_nvNow && nightVision) nv.StartSwitch(true);
            if (thUsable && !_thNow && thermal) th.StartSwitch(true);

            if (nvUsable) _nvNow = nightVision;
            if (thUsable) _thNow = thermal;
        }
    }
}
