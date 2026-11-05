//====================[ Imports ]====================
using System.Collections;
using EFT;
using EFT.UI;

namespace KeepMeAlive.Runtime.Lifecycle
{
    //====================[ RaidStartupTasks ]====================
    // One-time-per-raid setup for the local player, run from RaidLifecycle.OnGameStarted.
    internal static class RaidStartupTasks
    {
        public static void OnLocalRaidStart(Player localPlayer)
        {
            var st = RMSession.GetPlayerState(localPlayer.ProfileId);
            st.LivesRemaining = SyncedServerConfigStore.Config.Gameplay.Downed.MaxLivesPerRaid;

            DownedMovementController.ScrubDownedInputLocks();

            StimSounds.Preload();

            // Inject the revive cooldown icon eagerly so health UI can show it.
            ReviveCooldownIcon.EnsureIconInjected();
            // Its tooltip name, in the player's language (the game's is known by now).
            PostReviveEffects.RefreshCooldownEffectName();

            // Initialize UI panel references during raid load.
            Plugin.StaticCoroutineRunner.StartCoroutine(WarmupUiPanelsCoroutine());
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

                    if (uiHierarchyReady && Hud.Warmup())
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
