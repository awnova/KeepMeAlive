//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.Communications;
using EFT.UI;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Features
{
    //====================[ RaidStartupTasks ]====================
    // One-time-per-raid setup for the local player, run from FikaRaidLifecycle.OnRaidStarted
    // (FikaRaidStartedEvent) once that player is known and the host is not headless.
    internal static class RaidStartupTasks
    {
        public static void OnLocalRaidStart(Player localPlayer)
        {
            // Read the server's per-raid lives value asynchronously.
            var st = RMSession.GetPlayerState(localPlayer.ProfileId);
            st.LivesRemaining = SyncedServerConfigStore.Config.Gameplay.Revival.MaxLivesPerRaid;
            Plugin.StaticCoroutineRunner.StartCoroutine(FetchLivesCoroutine(localPlayer.ProfileId));

            WarnIfFikaReviveEnabled();

            DownedMovementController.ScrubDownedInputLocks();

            // Inject reviveItem icon eagerly so FikaHealthBar nameplates can show it.
            // FikaHealthBar.AddEffect() looks up effect.Type in _effectIcons when
            // EffectAddedEvent fires. Injection starts here and completes after hard settings load.
            ReviveItemCooldownIcon.EnsureIconInjected();

            // Initialize UI panel references during raid load.
            Plugin.StaticCoroutineRunner.StartCoroutine(WarmupUiPanelsCoroutine());
        }

        private static IEnumerator FetchLivesCoroutine(string profileId)
        {
            var task = RevivalAuthority.GetLivesAsync(profileId);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted || task.Result == null)
            {
                Plugin.LogSource.LogWarning($"[RaidStartupTasks] Could not read lives from server for {profileId}; using the configured maximum.");
                yield break;
            }

            if (RMSession.TryGetPlayerState(profileId, out var st))
            {
                st.LivesRemaining = task.Result.Value;
            }
        }

        // Fika ships its own downed/revive system that patches the same Kill() and
        // GetAvailableActions() methods; running both makes the two flows fight each other.
        private static void WarnIfFikaReviveEnabled()
        {
            try
            {
                if (!global::Fika.Core.FikaPlugin.Instance.Settings.ReviveConfig.Enabled) return;

                const string msg = "KeepMeAlive: Fika's built-in revive system is enabled on the server. Disable it (Fika server config) - it conflicts with KeepMeAlive.";
                Plugin.LogSource.LogError(msg);
                NotificationManager.DisplayWarningNotification(msg, ENotificationDurationType.Long);
            }
            catch (Exception ex)
            {
                RevivalDebugLog.LogDebug($"[RaidStartupTasks] Could not read Fika revive config: {ex.Message}");
            }
        }

        private static IEnumerator WarmupUiPanelsCoroutine()
        {
            const int maxFrames = 3600; // ~60 seconds at 60 FPS

            for (int i = 0; i < maxFrames; i++)
            {
                if (MonoBehaviourSingleton<GameUI>.Instantiated)
                {
                    var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
                    bool uiHierarchyReady = gameUi != null
                        && gameUi.gameObject.activeInHierarchy
                        && gameUi.LocationTransitTimerPanel != null;

                    if (uiHierarchyReady && VFX_UI.TryWarmupPanels())
                    {
                        Plugin.LogSource.LogDebug("[RaidStartupTasks] UI panel warmup complete (GameUI hierarchy active).");
                        yield break;
                    }
                }

                yield return null;
            }

            Plugin.LogSource.LogWarning("[RaidStartupTasks] UI panel warmup timed out before GameUI hierarchy became ready; runtime panel retry remains enabled.");
        }
    }
}
