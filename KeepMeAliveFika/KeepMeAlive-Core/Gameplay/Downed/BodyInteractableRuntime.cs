//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.UI;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ BodyInteractableRuntime ]====================
    // Tracks attached body interactables and routes their actions.
    internal static class BodyInteractableRuntime
    {
        //====================[ Cache ]====================
        private static readonly Dictionary<string, BodyInteractable> Cache = new Dictionary<string, BodyInteractable>();

        // Player objects an attach was started for this raid (a reconnect brings a new object under the same id).
        private static readonly Dictionary<string, Player> Attempted = new Dictionary<string, Player>();
        // Set once the raid has started; players spawning after that (join in progress, reconnects) are attached
        // from EnsureAttached.
        private static bool _raidStarted;

        public static bool HasAny => Cache.Count > 0;

        public static bool Has(string profileId) =>
            !string.IsNullOrEmpty(profileId) && Cache.TryGetValue(profileId, out var bi) && bi != null;

        public static bool TryGet(string profileId, out BodyInteractable interactable)
        {
            interactable = null;
            return !string.IsNullOrEmpty(profileId) && Cache.TryGetValue(profileId, out interactable) && interactable != null;
        }

        //====================[ Public API ]====================
        public static bool TryRouteActions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState result)
        {
            return TryRouteInteractive(owner, interactive, ref result);
        }

        // Routes actions for BodyInteractable instances.
        private static bool TryRouteInteractive(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState result)
        {
            if (interactive is not BodyInteractable body) return false;
            result = body.GetActions(owner);
            return true;
        }

        //====================[ Lifecycle ]====================
        // Attaches a BodyInteractable to every already-spawned remote human at raid start.
        // Called from FikaRaidLifecycle.OnRaidStarted (skipped entirely on a headless host,
        // which has no human at the keyboard to revive/heal/loot anyone).
        private static readonly List<Player> HumanBuffer = new List<Player>();

        public static void AttachToRemoteHumans()
        {
            if (!FikaBridge.TryGetHumanPlayers(HumanBuffer))
            {
                Plugin.LogSource.LogWarning("[AttachToRemoteHumans] CoopHandler not available");
                return;
            }

            _raidStarted = true;
            RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] HumanPlayers count={HumanBuffer.Count}");
            foreach (var player in HumanBuffer)
            {
                if (!FikaBridge.IsObservedPeer(player))
                {
                    RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] SKIP local player {player.Id} ({player.GetType().Name})");
                    continue;
                }

                RevivalDebugLog.LogDebug($"[AttachToRemoteHumans] Attaching to remote player {player.Id} ({player.Profile?.Nickname})");
                AttachToPlayer(player);
            }
        }

        // Per tick for every non-AI player: a remote human that spawned after the raid started has no interactable.
        public static void EnsureAttached(Player player)
        {
            if (!_raidStarted || player == null || player.IsYourPlayer || !PlayerUtils.IsHuman(player)) return;
            if (!FikaBridge.IsObservedPeer(player)) return;
            if (string.IsNullOrEmpty(player.ProfileId) || Attempted.TryGetValue(player.ProfileId, out var tried) && ReferenceEquals(tried, player)) return;
            if (!PlayerUtils.IsAlive(player)) return;

            AttachToPlayer(player);
        }

        public static void AttachToPlayer(Player player)
        {
            try
            {
                if (player != null && !string.IsNullOrEmpty(player.ProfileId)) Attempted[player.ProfileId] = player;
                if (player == null || player.gameObject == null)
                {
                    Plugin.LogSource.LogError("AttachToPlayer: Player or transform is null");
                    return;
                }

                BodyInteractable.AttachToPlayer(player);
                RevivalDebugLog.LogDebug($"Initiated BodyInteractable attachment routine for PlayerId {player.Id}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"AttachToPlayer error for player {(player != null ? player.Id.ToString() : "null")}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void Register(string profileId, BodyInteractable interactable)
        {
            if (string.IsNullOrEmpty(profileId) || interactable == null) return;
            Cache[profileId] = interactable;
        }

        //====================[ Cleanup ]====================
        public static void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            Cache.Remove(playerId);
        }

        // Only drops the entry if it still points at this instance (a newer one may have replaced it).
        public static void Remove(string playerId, BodyInteractable instance)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (Cache.TryGetValue(playerId, out var cached) && ReferenceEquals(cached, instance))
                Cache.Remove(playerId);
        }

        public static void Clear()
        {
            var live = new List<BodyInteractable>(Cache.Values);
            Cache.Clear();
            Attempted.Clear();
            _raidStarted = false;
            foreach (var bi in live)
            {
                if (bi != null) UnityEngine.Object.Destroy(bi.gameObject);
            }
        }
    }
}
