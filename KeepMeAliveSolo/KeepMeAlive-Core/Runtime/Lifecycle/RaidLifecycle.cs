//====================[ Imports ]====================
using EFT;

namespace KeepMeAlive.Runtime.Lifecycle
{
    //====================[ RaidLifecycle ]====================
    // Owns the single-player raid lifecycle driven by GameWorld.OnGameStarted,
    // BaseLocalGame<EftGamePlayerOwner>.Stop, and GameWorld.Dispose.
    internal static class RaidLifecycle
    {
        private static GameWorld _resetFor;

        public static void OnGameStarted(GameWorld world)
        {
            ResetOncePerRaid(world, "raid-started");

            SyncedServerConfigStore.EnsureLoaded("raid-started");

            var localPlayer = world != null && world.MainPlayer != null ? world.MainPlayer : PlayerUtils.GetYourPlayer();
            RevivalDebugLog.LogDebug($"[OnRaidStarted] IsScav={localPlayer?.Side == EPlayerSide.Savage}");

            if (localPlayer != null)
            {
                RaidStartupTasks.OnLocalRaidStart(localPlayer);
            }
        }

        private static void ResetOncePerRaid(GameWorld world, string reason)
        {
            if (world == null || ReferenceEquals(world, _resetFor)) return;
            _resetFor = world;
            DownedStateController.IsRaidEnding = false;
            DownedStateController.ResetForRaidBoundary(reason);
        }

        public static void OnGameEnded(string reason)
        {
            DownedStateController.IsRaidEnding = true;
            DownedStateController.ResetForRaidBoundary(reason);
            _resetFor = null;
        }
    }
}
