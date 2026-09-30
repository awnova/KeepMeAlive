//====================[ Imports ]====================
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using Fika.Core;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;

namespace KeepMeAlive.Patches
{
    //====================[ FikaOverlayPatch ]====================
    // Patches FikaHealthBar.CreateHealthBar so the per-player overlay controller is
    // attached once the plate and observed-player references are ready.
    internal class FikaOverlayPatch : ModulePatch
    {
        // Maps each nameplate to its overlay controller for direct lookup during UpdateHealth().
        internal static readonly ConditionalWeakTable<FikaHealthBar, FikaOverlayController> Controllers = new();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FikaHealthBar), "CreateHealthBar");

        [PatchPostfix]
        private static void PatchPostfix(FikaHealthBar __instance, PlayerPlateUI ____playerPlate, ObservedPlayer ____currentPlayer)
        {
            try
            {
                if (__instance == null || ____playerPlate == null || ____currentPlayer == null) return;
                var controller = __instance.gameObject.AddComponent<FikaOverlayController>();
                controller.Initialize(__instance, ____playerPlate, ____currentPlayer);
                Controllers.Remove(__instance);
                Controllers.Add(__instance, controller);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[FikaOverlayPatch] Postfix error: {ex.Message}");
            }
        }
    }

    //====================[ FikaHealthBarUpdateHealthPatch ]====================
    // Coordinates Fika's health-bar refresh with the KeepMeAlive downed/reviving timer overlay.
    internal class FikaHealthBarUpdateHealthPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FikaHealthBar), "UpdateHealth");

        [PatchPrefix]
        private static bool Prefix(FikaHealthBar __instance)
        {
            return !FikaOverlayPatch.Controllers.TryGetValue(__instance, out var controller) || !controller.IsActive;
        }
    }

    //====================[ FikaOverlayController ]====================
    // Per-player MonoBehaviour that repurposes the Fika nameplate's name text and
    // health bar/number into a downed/reviving timer, driven by RMSession state.
    // Runs in LateUpdate to override Fika's Update-phase writes. Everything else about
    // the plate (effects, limbs, faction icons, occlusion/ADS/distance fade) is left to Fika.
    internal sealed class FikaOverlayController : MonoBehaviour
    {
        private const string BleedingOutText = "BLEEDING OUT";
        private const string RevivingText = "REVIVING";

        private FikaHealthBar _healthBar;
        private PlayerPlateUI _plate;
        private ObservedPlayer _player;
        private string _profileId;

        public bool IsActive { get; private set; }
        private RMState _shownState = RMState.None;

        // Cached so the name can be restored exactly once the player is no longer critical.
        private string _origName;
        private Color _origNameColor;
        private FontStyles _origNameStyle;

        private float _bleedRemaining;
        private float _lastSyncedCritical;
        private float _reviveElapsed;
        private float _reviveDuration;

        public void Initialize(FikaHealthBar healthBar, PlayerPlateUI plate, ObservedPlayer player)
        {
            _healthBar = healthBar;
            _plate = plate;
            _player = player;
            _profileId = player.ProfileId;
        }

        private void LateUpdate()
        {
            // FikaHealthBar was destroyed (player died/extracted) - clean up.
            if (_healthBar == null)
            {
                Destroy(this);
                return;
            }

            if (!RMSession.TryGetPlayerState(_profileId, out var st) || !st.IsCritical)
            {
                if (IsActive) Exit();
                return;
            }

            if (!IsActive || st.State != _shownState)
            {
                Enter(st);
            }

            UpdateTimer(st, Time.deltaTime);
        }

        private void Enter(RMPlayer st)
        {
            if (!IsActive)
            {
                _origName = _plate.playerNameScreen.text;
                _origNameColor = _plate.playerNameScreen.color;
                _origNameStyle = _plate.playerNameScreen.fontStyle;
            }

            _plate.playerNameScreen.fontStyle = FontStyles.Bold | FontStyles.UpperCase;

            if (st.State == RMState.BleedingOut)
            {
                _plate.playerNameScreen.color = Color.red;
                _plate.playerNameScreen.SetText(BleedingOutText);
                _bleedRemaining = st.CriticalTimer;
                _lastSyncedCritical = st.CriticalTimer;
            }
            else
            {
                _plate.playerNameScreen.color = Color.green;
                _plate.playerNameScreen.SetText(RevivingText);
                _reviveElapsed = 0f;
                _reviveDuration = RevivePolicy.GetProgressDuration((ReviveSource)st.ReviveRequestedSource);
            }

            _shownState = st.State;
            IsActive = true;
            ShowTimerWidgets();
        }

        private void Exit()
        {
            if (!IsActive) return;
            IsActive = false;

            _plate.playerNameScreen.SetText(_origName);
            _plate.playerNameScreen.color = _origNameColor;
            _plate.playerNameScreen.fontStyle = _origNameStyle;

            RestoreHealth();
        }

        private void UpdateTimer(RMPlayer st, float deltaTime)
        {
            float fill;
            Color barColor;

            if (st.State == RMState.BleedingOut)
            {
                // A resync (or a fresh BleedingOut packet) re-seeds the countdown; otherwise tick locally.
                if (!Mathf.Approximately(st.CriticalTimer, _lastSyncedCritical))
                {
                    _lastSyncedCritical = st.CriticalTimer;
                    _bleedRemaining = st.CriticalTimer;
                }
                else
                {
                    _bleedRemaining -= deltaTime;
                }

                var total = SyncedServerConfigStore.Config.Gameplay.Revival.CriticalStateSeconds;
                fill = total > 0f ? Mathf.Clamp01(_bleedRemaining / total) : 0f;
                barColor = Color.red;
            }
            else
            {
                _reviveElapsed += deltaTime;
                fill = _reviveDuration > 0f ? Mathf.Clamp01(_reviveElapsed / _reviveDuration) : 1f;
                barColor = FikaPlugin.Instance.Settings.FullHealthColor.Value;
            }

            if (FikaPlugin.Instance.Settings.UseHealthNumber.Value)
            {
                _plate.SetHealthNumberText(Mathf.RoundToInt(fill * 100f));
            }
            else
            {
                _plate.healthBarScreen.fillAmount = fill;
                barColor.a = _plate.healthBarScreen.color.a;
                _plate.healthBarScreen.color = barColor;
            }
        }

        private void ShowTimerWidgets()
        {
            bool useNumber = FikaPlugin.Instance.Settings.UseHealthNumber.Value;
            SetActive(_plate.healthNumberScreen.gameObject, useNumber);
            SetActive(_plate.healthNumberBackgroundScreen.gameObject, useNumber);
            SetActive(_plate.healthBarScreen.gameObject, !useNumber);
            SetActive(_plate.healthBarBackgroundScreen.gameObject, !useNumber);
        }

        // Restores Fika's own health visibility/value so the real health shows again immediately,
        // mirroring FikaHealthBar.SetPlayerPlateHealthVisibility/UpdateHealth (which stay blocked
        // by FikaHealthBarUpdateHealthPatch while a plate is critical).
        private void RestoreHealth()
        {
            var settings = FikaPlugin.Instance.Settings;
            bool hidden = settings.HideHealthBar.Value;
            bool useNumber = settings.UseHealthNumber.Value;

            SetActive(_plate.healthNumberScreen.gameObject, !hidden && useNumber);
            SetActive(_plate.healthNumberBackgroundScreen.gameObject, !hidden && useNumber);
            SetActive(_plate.healthBarScreen.gameObject, !hidden && !useNumber);
            SetActive(_plate.healthBarBackgroundScreen.gameObject, !hidden && !useNumber);

            if (hidden || _player.HealthController == null) return;

            var health = _player.HealthController.GetBodyPartHealth(EBodyPart.Common, true);
            var current = health.Current;
            var max = health.Maximum;

            if (useNumber)
            {
                _plate.SetHealthNumberText((int)Math.Round(current / max * 100));
            }
            else
            {
                var normalized = Mathf.Clamp01(current / max);
                _plate.healthBarScreen.fillAmount = normalized;

                var color = Color.Lerp(settings.LowHealthColor.Value, settings.FullHealthColor.Value, normalized);
                color.a = _plate.healthBarScreen.color.a;
                _plate.healthBarScreen.color = color;
            }
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }

        private void OnDestroy()
        {
            if (IsActive) Exit();
        }
    }
}
