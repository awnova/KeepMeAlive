//====================[ Imports ]====================
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ ModUtils ]====================
    internal static class ModUtils
    {
        //====================[ Network / HTTP ]====================

        // SPT wraps all HTTP responses as: {"err":0,"errmsg":"","data":{...}}. We must extract the inner "data" field before deserializing into T.
        private class SptEnvelope<T>
        {
            public T data { get; set; }
        }

        // Blocking HTTP call - never call from the Unity main thread (see RevivalAuthority).
        // Returns the server response and propagates transport or parsing exceptions to the caller.
        public static T ServerRoute<T>(string url, object data = default)
        {
            string jsonReq = JsonConvert.SerializeObject(data);
            string jsonRes = RequestHandler.PostJson(url, jsonReq);

            var envelope = JsonConvert.DeserializeObject<SptEnvelope<T>>(jsonRes);
            if (envelope != null && envelope.data != null)
            {
                return envelope.data;
            }

            // Fallback: response was already unwrapped (plain JSON).
            return JsonConvert.DeserializeObject<T>(jsonRes);
        }

        //====================[ Player Lookups ]====================
        public static Player GetYourPlayer()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            return (player != null && player.IsYourPlayer) ? player : null;
        }

        public static Player GetPlayerById(string id)
        {
            if (string.IsNullOrEmpty(id) || !Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            return Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(id);
        }

        public static string GetPlayerDisplayName(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                return playerId;
            }

            var p = GetPlayerById(playerId);
            var nick = p?.Profile?.Nickname;

            return string.IsNullOrEmpty(nick) ? playerId : NormalizeDisplayName(nick);
        }

        // Tarkov nicknames arrive in whatever casing the player typed (all caps, all
        // lowercase, mixed) - normalize to Title Case so revive/heal messages read the
        // same regardless of how a given player set their own name.
        private static string NormalizeDisplayName(string name) =>
            char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();

        //====================[ Revive Item ]====================
        private static Item FindReviveItem(Player player)
        {
            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                string templateId = SyncedServerConfigStore.Config.RevivalItem.TemplateId;
                if (string.IsNullOrEmpty(templateId)) return null;

                foreach (var it in items)
                {
                    if (it != null && string.Equals(it.StringTemplateId, templateId, StringComparison.OrdinalIgnoreCase))
                    {
                        return it;
                    }
                }

                Plugin.LogSource.LogDebug($"FindReviveItem: no item matching {templateId} found for {player?.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"FindReviveItem error: {ex.Message}");
            }

            return null;
        }

        public static bool HasReviveItem(Player player) => FindReviveItem(player) != null;

        // Consumes one revive item from the local player's inventory as a network transaction
        // on their controller. Resolves the item after waiting for the inventory to be free.
        // onDone reports whether the item was actually removed.
        public static IEnumerator ConsumeReviveItemCoroutine(Player player, string label, Action<bool> onDone)
        {
            if (player == null || !player.IsYourPlayer)
            {
                onDone?.Invoke(false);
                yield break;
            }

            yield return WaitForInventoryFree(player, 2f, label);

            var controller = player.InventoryController;
            var item = FindReviveItem(player);
            if (controller == null || item == null)
            {
                Plugin.LogSource.LogWarning($"[{label}] No revive item left to consume for {player.ProfileId}");
                onDone?.Invoke(false);
                yield break;
            }

            OperationResult operation = item.StackObjectsCount > 1
                ? (OperationResult)ItemManipulator.SplitToNowhere(item, 1, controller, controller, simulate: true)
                : (OperationResult)ItemManipulator.Remove(item, controller, simulate: true);

            if (operation.Failed)
            {
                Plugin.LogSource.LogWarning($"[{label}] Could not build remove operation for {item.Id}: {operation.Error}");
                onDone?.Invoke(false);
                yield break;
            }

            var task = controller.TryRunNetworkTransaction(operation);
            while (!task.IsCompleted) yield return null;

            bool failed = task.IsFaulted || task.Result == null || task.Result.Failed;
            if (failed)
            {
                Plugin.LogSource.LogWarning($"[{label}] Revive item removal failed for {item.Id}: {(task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result?.Error)}");
            }
            onDone?.Invoke(!failed);
        }

        //====================[ Inventory State ]====================
        // True when the player's inventory controller has an operation in flight, such as an
        // item apply/use or pending equip.
        private static bool HasAnyActiveInventoryEvent(Player player)
        {
            var events = (player?.InventoryController as ItemController)?.ActiveEvents;
            if (events == null) return false;
            foreach (var activeEvent in events)
            {
                if (activeEvent != null) return true;
            }
            return false;
        }

        // True while the item is being used (e.g. a med currently applied by someone).
        public static bool IsItemInUse(Player owner, Item item)
        {
            var events = (owner?.InventoryController as ItemController)?.ActiveEvents;
            if (events == null || item == null) return false;
            foreach (var activeEvent in events)
            {
                if (activeEvent?.Item == item) return true;
            }
            return false;
        }

        // Yields until the player has no active inventory event or timeoutSeconds elapses.
        // Proceeds after the timeout.
        public static IEnumerator WaitForInventoryFree(Player player, float timeoutSeconds, string label)
        {
            float elapsed = 0f;
            while (player != null && HasAnyActiveInventoryEvent(player) && elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (player != null && elapsed >= timeoutSeconds && HasAnyActiveInventoryEvent(player))
            {
                Plugin.LogSource.LogWarning($"[{label}] WaitForInventoryFree timed out after {timeoutSeconds}s; proceeding anyway.");
            }
        }

        //====================[ Team Heal ]====================
        // Returns true if the item exists, belongs to the healer, and has resource remaining.
        public static bool IsTeamHealItemValid(Player healer, Item item, out string reason)
        {
            reason = null;
            if (item == null || item.Parent == null || healer?.InventoryController == null)
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            if (!ReferenceEquals(item.Parent.GetOwner(), healer.InventoryController))
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit != null && kit.HpResource < float.Epsilon)
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            var food = item.GetItemComponent<FoodDrinkComponent>();
            if (food != null && food.HpPercent < float.Epsilon)
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            return true;
        }
    }
}
