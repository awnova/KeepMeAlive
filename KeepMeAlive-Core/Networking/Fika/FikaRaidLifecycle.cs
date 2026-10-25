using Comfort.Common;
//====================[ Imports ]====================
using EFT;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;

namespace KeepMeAlive.Networking.Fika
{
    //====================[ FikaRaidLifecycle ]====================
    // Owns the raid lifecycle: plugin-enable subscription, network-manager creation (packet
    // registration), and the raid-started/game-world-started/game-ended events. Per-raid setup
    // that isn't about the network itself (lives fetch, UI warmup) lives in RaidStartupTasks;
    // body-interactable attachment lives in BodyInteractableRuntime.
    internal static class FikaRaidLifecycle
    {
        //====================[ Init ]====================
        // Event subscriptions are installed once for the plugin lifetime.
        private static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnFikaNetManagerCreated);
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(OnGameWorldStarted);
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            Plugin.LogSource.LogInfo("Fika integration initialized!");
        }

        //====================[ Network Manager Created — Packet Registration ]====================
        private static void OnFikaNetManagerCreated(FikaNetworkManagerCreatedEvent managerCreatedEvent)
        {
            RevivePacketHandlers.Register(managerCreatedEvent.Manager);
            TeamHealPacketHandlers.Register(managerCreatedEvent.Manager);
            DragPacketHandlers.Register(managerCreatedEvent.Manager);
            DefibPacketHandlers.Register(managerCreatedEvent.Manager);
        }

        //====================[ Raid Started — Local Setup + Attach Body Interactables ]====================
        private static void OnRaidStarted(FikaRaidStartedEvent e)
        {
            // Usually the first of the two start events (the countdown ends before the world's async setup does), so the
            // clean slate has to come before this raid's setup below, not after it.
            if (Singleton<GameWorld>.Instantiated) ResetOncePerRaid(Singleton<GameWorld>.Instance, "raid-started");

            SyncedServerConfigStore.EnsureLoaded("raid-started");
            RevivalDebugLog.LogDebug($"[OnRaidStarted] IsScav={FikaBridge.IsScav()}");

            // A headless host has no human at the keyboard to revive/heal/loot anyone.
            if (FikaBridge.IsHeadless()) return;

            var localPlayer = PlayerUtils.GetYourPlayer();
            if (localPlayer != null)
            {
                RaidStartupTasks.OnLocalRaidStart(localPlayer);
            }

            BodyInteractableRuntime.AttachToRemoteHumans();
        }

        private static void OnGameWorldStarted(GameWorldStartedEvent e) => ResetOncePerRaid(e.GameWorld, "game-world-started");

        // A clean slate at raid start, even if the previous raid ended without FikaGameEndedEvent (crash/disconnect).
        // Fika raises FikaRaidStartedEvent (end of the deploy countdown) and GameWorldStartedEvent (end of the world's
        // async setup) from separate paths, in either order; whichever comes first resets, once per game world. Resetting
        // on the later one would wipe what OnRaidStarted just set up (the lives from the server, the body interactables).
        private static GameWorld _resetFor;

        private static void ResetOncePerRaid(GameWorld world, string reason)
        {
            if (world == null || ReferenceEquals(world, _resetFor)) return;
            _resetFor = world;
            DownedStateController.IsRaidEnding = false;
            DownedStateController.ResetForRaidBoundary(reason);
        }

        private static void OnGameEnded(FikaGameEndedEvent e)
        {
            DownedStateController.IsRaidEnding = true;
            DownedStateController.ResetForRaidBoundary("game-ended");
            // The next raid's world is a new object anyway; this just stops holding the old one.
            _resetFor = null;
        }
    }
}
